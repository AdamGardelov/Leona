"""Leona's photo service: removes things from photos, on this computer.

The backend posts {"input": path, "output": path, "remove": "tree", "all": false} to /remove. The object
is found from its description (Grounding DINO), outlined (SAM) and painted over with the surrounding
background (LaMa). Only the area around it is redrawn, so the rest of the photo keeps its full
resolution. Everything runs on the processor, so it never competes with Ollama for graphics memory;
the models load on first use and are let go after ten idle minutes.
"""

import gc
import json
import os
import pathlib
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
import torch
from PIL import Image, ImageFilter, ImageOps

HERE = pathlib.Path(__file__).resolve().parent
PORT = int(os.environ.get('PHOTO_PORT', '5090'))
MODELS = pathlib.Path(os.environ.get('PHOTO_MODELS', HERE / 'models'))
# Files may only be read and written below this folder (Leona's uploads) when it is set.
ROOT = os.environ.get('PHOTO_ROOT')
os.environ.setdefault('HF_HOME', str(MODELS / 'huggingface'))

from transformers import (  # noqa: E402  (HF_HOME must be set first)
    AutoModelForZeroShotObjectDetection,
    AutoProcessor,
    SamModel,
    SamProcessor,
)

DETECTOR = 'IDEA-Research/grounding-dino-tiny'
SEGMENTER = 'facebook/sam-vit-base'
LAMA_URL = 'https://github.com/enesmsahin/simple-lama-inpainting/releases/download/v0.1.0/big-lama.pt'
# Finding and outlining work on a smaller copy; the photo itself is only touched around the object.
WORK_SIDE = 1600
PAINT_SIDE = 1024
# LaMa continues a scene best when the hole is at most this many pixels across, so large holes are
# painted on a smaller copy and scaled back; small ones keep their full detail.
HOLE_SIDE = 384
IDLE_SECONDS = 600

torch.set_num_threads(os.cpu_count() or 4)


class Models:
    """Loaded on first use and dropped when idle, so they hold no memory between edits."""

    def __init__(self):
        self.lock = threading.Lock()
        self.loaded = None
        self.used = 0.0

    def get(self):
        if self.loaded is None:
            started = time.time()
            detector_processor = AutoProcessor.from_pretrained(DETECTOR)
            detector = AutoModelForZeroShotObjectDetection.from_pretrained(DETECTOR).eval()
            segmenter_processor = SamProcessor.from_pretrained(SEGMENTER)
            segmenter = SamModel.from_pretrained(SEGMENTER).eval()
            lama_path = MODELS / 'big-lama.pt'
            if not lama_path.exists():
                MODELS.mkdir(parents=True, exist_ok=True)
                partial = lama_path.with_suffix('.part')
                urllib.request.urlretrieve(LAMA_URL, partial)
                partial.rename(lama_path)
            lama = torch.jit.load(str(lama_path), map_location='cpu').eval()
            self.loaded = (detector_processor, detector, segmenter_processor, segmenter, lama)
            print(f'Models loaded in {time.time() - started:.1f} s', flush=True)
        self.used = time.time()
        return self.loaded

    def release_when_idle(self):
        while True:
            time.sleep(60)
            with self.lock:
                if self.loaded is not None and time.time() - self.used > IDLE_SECONDS:
                    self.loaded = None
                    gc.collect()
                    print('Models released', flush=True)


models = Models()


def shrink(image, side):
    scale = min(1.0, side / max(image.size))
    if scale == 1.0:
        return image, 1.0
    size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))
    return image.resize(size, Image.Resampling.LANCZOS), scale


def detect(loaded, image, labels, every):
    """Boxes (in the given image's pixels) of what the labels describe: the best match for each label,
    or every match."""
    processor, model = loaded[0], loaded[1]
    inputs = processor(images=image, text=[labels], return_tensors='pt')
    with torch.inference_mode():
        outputs = model(**inputs)
    found = processor.post_process_grounded_object_detection(
        outputs,
        inputs.input_ids,
        threshold=0.3,
        text_threshold=0.25,
        target_sizes=[(image.height, image.width)],
    )[0]
    names = found.get('text_labels') or found.get('labels') or []
    hits = sorted(
        (
            {'label': str(name), 'score': round(float(score), 2), 'box': [float(v) for v in box]}
            for box, score, name in zip(found['boxes'], found['scores'], names)
        ),
        key=lambda hit: hit['score'],
        reverse=True,
    )
    if every:
        return hits
    best = {}
    for hit in hits:
        label = next((l for l in labels if l in hit['label']), hit['label'])
        best.setdefault(label, hit)
    return list(best.values())


def outline(loaded, image, boxes):
    """One mask covering every box's object, from SAM's best guess per box."""
    processor, model = loaded[2], loaded[3]
    inputs = processor(images=image, input_boxes=[boxes], return_tensors='pt')
    with torch.inference_mode():
        outputs = model(**inputs, multimask_output=True)
    masks = processor.image_processor.post_process_masks(
        outputs.pred_masks, inputs['original_sizes'], inputs['reshaped_input_sizes']
    )[0]
    best = outputs.iou_scores[0].argmax(-1)
    union = np.zeros((image.height, image.width), dtype=bool)
    for i in range(len(boxes)):
        union |= masks[i, best[i]].numpy().astype(bool)
    return union


def grow(mask, pixels):
    """Widens the mask so edges, thin twigs and soft shadows go too."""
    if pixels < 1:
        return mask
    size = pixels * 2 + 1
    tensor = torch.from_numpy(mask.astype(np.float32))[None, None]
    grown = torch.nn.functional.max_pool2d(tensor, size, stride=1, padding=pixels)
    return grown[0, 0].numpy() > 0.5


def paint(loaded, image, mask):
    """Fills the masked area from its surroundings and returns the whole photo."""
    lama = loaded[4]
    ys, xs = np.nonzero(mask)
    top, bottom, left, right = ys.min(), ys.max(), xs.min(), xs.max()
    # Background around the hole gives LaMa something to continue.
    margin = int(max(bottom - top, right - left) * 0.6) + 32
    box = (
        max(0, left - margin),
        max(0, top - margin),
        min(image.width, right + margin + 1),
        min(image.height, bottom + margin + 1),
    )
    crop = image.crop(box)
    crop_mask = Image.fromarray(mask[box[1] : box[3], box[0] : box[2]].astype(np.uint8) * 255)
    hole = max(bottom - top, right - left) + 1
    small, scale = shrink(crop, min(PAINT_SIDE, max(crop.size) * HOLE_SIDE / hole))
    small_mask = crop_mask.resize(small.size, Image.Resampling.NEAREST) if scale < 1.0 else crop_mask

    pixels = np.asarray(small, dtype=np.float32) / 255.0
    holes = (np.asarray(small_mask) > 127).astype(np.float32)
    height, width = holes.shape
    pad_h, pad_w = (8 - height % 8) % 8, (8 - width % 8) % 8
    pixels = np.pad(pixels, ((0, pad_h), (0, pad_w), (0, 0)), mode='reflect')
    holes = np.pad(holes, ((0, pad_h), (0, pad_w)), mode='reflect')
    with torch.inference_mode():
        result = lama(
            torch.from_numpy(pixels).permute(2, 0, 1)[None],
            torch.from_numpy(holes)[None, None],
        )
    filled = (result[0].permute(1, 2, 0).numpy()[:height, :width] * 255).clip(0, 255).astype(np.uint8)
    painted = Image.fromarray(filled).resize(crop.size, Image.Resampling.LANCZOS)

    # Only the hole is replaced, with a soft edge; everything else keeps the original pixels.
    feather = max(2, round(max(crop.size) / 300))
    alpha = crop_mask.filter(ImageFilter.GaussianBlur(feather))
    crop.paste(painted, (0, 0), alpha)
    result_image = image.copy()
    result_image.paste(crop, box[:2])
    return result_image


def inside_root(path):
    if not ROOT:
        return True
    return pathlib.Path(path).resolve().is_relative_to(pathlib.Path(ROOT).resolve())


def remove(request):
    source, target = request.get('input'), request.get('output')
    labels = [part.strip().lower() for part in str(request.get('remove', '')).split(',') if part.strip()]
    if not source or not target or not labels:
        raise ValueError('Give input, output and what to remove.')
    if not inside_root(source) or not inside_root(target):
        raise PermissionError('That file is outside the photo folder.')

    started = time.time()
    image = ImageOps.exif_transpose(Image.open(source)).convert('RGB')
    work, scale = shrink(image, WORK_SIDE)
    with models.lock:
        loaded = models.get()
        hits = detect(loaded, work, labels, bool(request.get('all')))
        if not hits:
            return 422, {'error': f'Could not find {", ".join(labels)} in the photo.'}
        mask = outline(loaded, work, [hit['box'] for hit in hits])
        mask = grow(mask, max(3, round(max(work.size) / 160)))
        if scale < 1.0:
            mask = np.asarray(Image.fromarray(mask.astype(np.uint8) * 255).resize(image.size, Image.Resampling.BILINEAR)) > 127
        result = paint(loaded, image, mask)
        models.used = time.time()

    pathlib.Path(target).parent.mkdir(parents=True, exist_ok=True)
    result.save(target, 'JPEG', quality=92)
    return 200, {
        'found': [{'label': hit['label'], 'score': hit['score']} for hit in hits],
        # Large holes are hard to fill convincingly; the share lets Leona say so.
        'area': round(float(mask.mean()), 3),
        'seconds': round(time.time() - started, 1),
        'width': result.width,
        'height': result.height,
    }


class Handler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass

    def reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == '/health':
            self.reply(200, {'ok': True, 'loaded': models.loaded is not None})
        else:
            self.reply(404, {'error': 'Not found.'})

    def do_POST(self):
        if self.path != '/remove':
            self.reply(404, {'error': 'Not found.'})
            return
        try:
            length = int(self.headers.get('Content-Length', '0'))
            status, body = remove(json.loads(self.rfile.read(length) or b'{}'))
            self.reply(status, body)
        except (ValueError, PermissionError, FileNotFoundError, OSError) as error:
            self.reply(400, {'error': str(error)})
        except Exception as error:  # A failed edit must not take the service down.
            print(f'Edit failed: {error!r}', flush=True)
            self.reply(500, {'error': 'The photo could not be edited.'})


if __name__ == '__main__':
    threading.Thread(target=models.release_when_idle, daemon=True).start()
    server = ThreadingHTTPServer(('127.0.0.1', PORT), Handler)
    print(f'Photo service on http://127.0.0.1:{PORT}', flush=True)
    server.serve_forever()
