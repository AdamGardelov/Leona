# Leona

A private AI assistant that runs on your own computer. Leona talks to local models through [Ollama](https://ollama.com), so conversations, mail and files never leave the house, and you reach it from your phone over [Tailscale](https://tailscale.com).

- **Chat** with streamed answers, tool steps you can follow, editing and regenerating, a run inspector and dark/light themes.
- **Web**: search through your own SearXNG and read pages, with sources; **Research** reads many pages and writes a report.
- **Projects and documents**: projects with their own instructions and files, and search by meaning in your own PDF, Word and text files.
- **Files and terminal**: read, create and edit files and run commands in folders you choose, each change approved by you.
- **Personal**: mail (IMAP/SMTP), calendars (CalDAV/iCloud), Home Assistant, Spotify, expenses.
- **Göteborg radars**: open jobs (Platsbanken and employers' career pages), things to do with small children, concerts and the SMHI forecast.
- **Automations**: scheduled prompts and page watches with push notifications, such as a morning brief or a daily job radar.
- **Phone**: a home-screen web app for iPhone, voice input with Swedish KB-Whisper on the computer, and Siri through a shortcut.
- **Profiles**: each person in the household gets their own chats, accounts and automations.

Built with ASP.NET Core 10 Minimal APIs, EF Core with SQLite, and React with TypeScript and Vite. It is made for Linux with GNOME, but everything except the top bar indicator runs wherever .NET, Node.js and Ollama do.

## Run

Requires the .NET 10 SDK, Node.js 22.12+ and Ollama with a model that supports tools; `gemma4:e4b` and `qwen3.5:9b` work well on an 8 GB graphics card.

```sh
git clone git@github.com:AdamGardelov/Leona.git
cd Leona
ollama pull gemma4:e4b
ollama pull qwen3-embedding:0.6b   # document search; optional
dotnet run --project backend
```

In a second terminal:

```sh
cd frontend
npm install
npm run dev
```

### Every day: start at login, with a top bar icon

```sh
./desktop/install.sh
```

This builds the backend, installs a systemd **user** service (`~/.config/systemd/user/leona.service`, no sudo) that starts Leona at login with phone access on, restarts it after a crash, and installs the GNOME top bar indicator (`desktop/gnome-extension`, GNOME 48–50). The indicator shows the Leona mark in mint while the backend runs and grey when it is off; its menu opens Leona, switches it on or off, restarts it (refused while an answer is being written, using `GET /api/status`, which only answers the computer itself) and turns autostart on or off. A second indicator shows Tailscale: connected or not, a switch for `tailscale up`/`down` (needs `sudo tailscale set --operator=$USER` once) and the tailnet's devices with their online state; click one to copy its name. GNOME on Wayland finds a new extension after logging out and in once.

```sh
systemctl --user restart leona     # after a backend build
journalctl --user -u leona -f      # logs
```

The service runs `backend/bin/Debug/net10.0/Harness.dll`, so `dotnet build backend` followed by a restart deploys a change. It starts when you log in; to have it start at boot without logging in, run `loginctl enable-linger`.

Open http://localhost:5173. Vite proxies `/api` to localhost:5080; the backend calls Ollama at localhost:11434. Override with `Ollama__BaseUrl`. Only Ollama is currently implemented as a model provider; any compatible installed model can be selected, and tools/thinking depend on its capabilities.

## Search and files

Web tools use SearXNG for search and read public HTML/text pages. Docker is needed only for the supplied SearXNG deployment; chat, SQLite and file tools run without Docker. Search results are discovery hints; only pages successfully read with `read_page` become sources, appended once by the backend.

For a fresh checkout, copy `.env.example` to `.env` and replace its secret with a random value (for example, `openssl rand -hex 32`). Then:

```sh
docker compose up -d searxng
```

If your account lacks Docker access, use `sudo docker compose up -d searxng`. Search listens on localhost:8080. `infra/searxng/settings.yml` enables HTML and JSON, adds Bing, Yahoo and DuckDuckGo's web endpoint to the default engines, and lets an engine that blocks requests back in after an hour; `Tools__SearxngUrl` overrides the backend URL. There is no Bing RSS fallback. External search engines can still rate-limit or reject requests.

`read_page` returns long pages in windows of the configured page excerpt (8,000 characters by default). The model can continue with `offset` or jump to matching passages with `find`; pages are cached for the rest of the run.

## Projects, documents and research

**Projects** gather chats that belong together, such as the housing association or a job search. Create one with + under Projects in the sidebar and give it instructions (who it is about, how answers should be) and files. Every chat in the project follows the instructions, and each message there brings the passages of the project's files that match it, so the model answers from them and names the file. A chat moves into or out of a project from its menu. Deleting a project keeps its chats and removes its files.

**Your own documents.** Leona searches PDF, Word and text files by meaning, not only by words: those in folders added under Settings › Folders, documents attached to chats, and project files. A small embedding model (`qwen3-embedding:0.6b`, about 1 GB next to the chat model, set with `Search:EmbeddingModel`) turns each passage of about 1000 characters into a vector, stored in SQLite; nothing leaves the computer. New and changed files are read within ten minutes, when no chat is running, or at once with Settings › Documents › Check for new documents. Questions about contracts, receipts, manuals and the like get the `search_documents` tool, which returns the best passages with the file and page; hidden folders, `node_modules`, build output and files over 20 MB are skipped. Scanned PDFs need OCR, which is not included.

**Research** (the toggle next to Web) answers one question thoroughly instead of quickly: the model plans four searches, reads the ten best pages from them (at most two per site), notes what each says about the question, searches once more for what is missing, and then writes a report with headings and numbered sources. It takes a few minutes with a small model and sends a notification when the report is ready, so the phone can be put away meanwhile. It needs SearXNG (above).

## Phone access

Leona can be used from a phone on the same Wi-Fi. Build the frontend once (`cd frontend && npm run build`; the backend serves `frontend/dist`), then start the backend with phone access on:

```sh
cd backend
Remote__Enabled=true dotnet run
```

It then listens on all interfaces on port 5080 (`Remote:Port`). On the computer, open Settings › Phone access and create a pairing code; open the address shown there on the phone and enter the code, or open the pairing link. Codes are six digits, single-use, valid for ten minutes and cancelled after ten wrong guesses. A paired device gets an HttpOnly session cookie (only its SHA-256 hash is stored) and can be removed under Phone access at any time.

The computer itself (loopback) needs no pairing. Other devices can load the app and the pairing screen, but every other API call needs a session. Pairing and opening folders to the tools can only be done on the computer. Requests must use `localhost` or one of the computer's private LAN addresses as host, which keeps DNS rebinding out; writes from other origins are refused.

If a firewall is active, allow the port from your network only, for example `sudo ufw allow from 192.168.1.0/24 to any port 5080 proto tcp`. Traffic is plain HTTP, so use phone access only on Wi-Fi you trust; anyone who captures the session cookie can act as you, including approving commands.

### Away from home (Tailscale)

[Tailscale](https://tailscale.com/kb/1017/install) gives the phone a private, encrypted route to the computer from anywhere, with HTTPS, which also unlocks push notifications and entering account passwords on the phone. On the computer:

```sh
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
sudo tailscale serve --bg 5080
```

Install the Tailscale app on the phone and sign in to the same tailnet. Leona then shows `https://<computer>.<tailnet>.ts.net` under Settings › Phone access (detected from `tailscale status`, or set `Remote:TailscaleHost`). Pair the phone once over that address. Tailscale Serve proxies from loopback; Leona only trusts forwarded headers from loopback, so proxied requests still need a paired session. `Remote__Enabled` is not needed for Tailscale; it only opens the LAN port.

On iPhone, open the address in Safari, then Share › Add to Home Screen. Push notifications on iOS only work from the home-screen app.

On phones Leona uses app sizes: 17 px text like iOS body text, larger icons and touch targets of at least 44 px. On iPhone the text follows Settings › Display & Brightness › Text Size, from the smallest setting up to the largest regular one (the larger accessibility sizes stop there so the layout holds). The computer keeps its compact sizes.

## Attachments

Paste images with Ctrl+V, drop files on the message box, or use the paper clip (on phones: + › Camera, Photos or Files). A photo in the chat opens full screen over it; close it with ×, a tap beside it, a swipe down or the back gesture, and save it with the share button (Spara bild on iPhone). Images are scaled to at most 1600 px and re-encoded as JPEG in the browser, so HEIC and WebP photos work too; they are sent to the model only if it reports vision support, and only with the message they belong to (later turns get a short placeholder). PDF, Word and plain-text documents are read into the message as untrusted content, up to the page excerpt size; with Files on, the full document stays readable through `read_document` in the `uploads` folder. Files are limited to 20 MB and eight per message, checked by content, and stored per profile in `backend/uploads/<profile>` (`Tools:UploadsPath`). Uploads no message refers to are removed after a day; project files stay with their project.

### Editing photos

Attach a photo and ask Leona to remove something: "ta bort trädet", "remove the people in the background", "the shadow too". A small photo service on this computer (`photo/server.py`) finds it from the description (Grounding DINO), outlines it (SAM) and fills in the background (LaMa). It runs on the processor, so it never competes with the chat model for graphics memory, and no picture leaves the computer. The edit is a new copy shown with the reply; the original is kept, and follow-ups work on the newest edit. An edit takes 10–20 seconds; the first after ten idle minutes also loads the models, and the very first downloads them (about 1.2 GB, to `photo/models`). Small things against sky, grass or a wall come out clean; large ones in front of something detailed come out softer.

Set up once (needs `python3-venv`):

```sh
python3 -m venv photo/.venv
photo/.venv/bin/pip install -r photo/requirements.txt
./desktop/install.sh                # adds the leona-photo service
systemctl --user start leona-photo
```

`Photo:Url` points the backend to the service (`http://127.0.0.1:5090/` by default). Without it running, Leona says how to start it.

## Files, commands and memory

Tools are opt-in per message with the Web, Files, Terminal and Personal toggles (Thinking turns on the model's reasoning). File tools work in `backend/workspace` (configurable via `Tools__WorkspacePath`) and in folders you add under Settings › Folders; each added folder gets a short name the model passes as `folder`. The model can never add folders. Every path is checked: no absolute paths, parent traversal, hidden segments (`.git`, `.env`, `.ssh`) or symbolic links.

| Tool | What it does | Approval |
| --- | --- | --- |
| `list_files`, `search_files` | List a folder; search file names and lines (skips hidden, `node_modules`, `bin`, `obj`, `dist` and similar) | No |
| `read_file` | UTF-8 text up to 2 MB, in line windows with `start_line` | No |
| `read_document` | PDF (via [PdfPig](https://github.com/UglyToad/PdfPig)) with page numbers, DOCX with headings, and plain text; `start` and `find` | No |
| `create_file` | A new file only; never overwrites | Yes, with a content preview |
| `edit_file` | Replaces one exact, unique piece of text | Yes, with a diff; refused if the file changed after the proposal |
| `run_command` | One bash command in a chosen folder, with a timeout (default 120 s) and bounded output | Yes, every time; `sudo`, `su`, `doas` and `pkexec` are refused |
| `search_documents` | Passages from your own documents and project files that match a question (see Projects, documents and research) | No |
| `save_memory`, `search_memory` | Short notes kept between conversations | No, unless the chat has read untrusted content (below); view and delete them in Settings |
| `save_skill` | A reusable procedure for a kind of task | Yes, always |

Each message only gets the tools it seems to need (room and device names from your Home Assistant count too, so "Höj skrivbordet" finds the home tools). The toggles decide which groups are allowed; within them, Swedish and English keywords pick families (mail, calendar, home, expenses, automations, documents, new files, edits, skills), the tools used in the previous turn stay available for follow-ups such as "ja, skicka det", and a read-only core is always offered (web, listing/searching/reading files, memory). The activity panel says "Offering 7 of 18 tools", and the run inspector lists them. A call to a tool that was not offered still works if its group is on.

**Voice.** The microphone next to Send records a question; the computer turns it into text with KB-Whisper (KBLab's Swedish Whisper, `backend/models/kb-whisper-small-q5_0.bin`, run on the processor through Whisper.net), so no audio leaves the house, and the question is sent at once. Its answer is read aloud with the device's own voice (Alva on iPhone); the speaker button reads any reply. Recording needs a secure page: the computer itself or the Tailscale https address. `Speech:ModelPath` points to another model; without one the microphone is hidden. The model (175 MB) is not in the repository; download it once:

```sh
mkdir -p backend/models
curl -L -o backend/models/kb-whisper-small-q5_0.bin \
  https://huggingface.co/KBLab/kb-whisper-small/resolve/main/ggml-model-q5_0.bin
```

**Siri.** Settings › Siri makes a key for an iPhone shortcut, "Leonafråga" (not "Fråga Leona", which Siri takes as sending a message): it dictates the question (Diktera text), posts it to `https://<tailscale name>/api/ask` with `Authorization: Bearer <key>` and a JSON body `{"text": …}`, and reads the plain-text answer aloud (Läs upp text). A key works only for `/api/ask`, never for chats or settings, and can be removed in Settings or under paired devices. Questions use the default model with Web and Personal on; a follow-up within 15 minutes continues the same chat. An answer that takes longer than 45 seconds, or needs approval, arrives as a notification.

**Unread chats.** A chat with a reply you have not seen yet, from a schedule, Siri or a run that finished while you were away, gets a blue dot in the sidebar (and on the menu button on phones); the number of unread chats shows on the app icon where the browser supports it. A chat counts as read once it is open on screen, on every device.

**Small-model safeguards.** If a reply comes back empty, or ends by announcing a step ("I'll check…") without taking it, Leona asks the model once to continue. Tool results carry a short reminder to reply in the language of the user's message.

**Untrusted content.** Web pages, mail, calendars, files, attachments, recalled tool excerpts and memories can hide instructions. Once a run has read any of them, two otherwise free actions need approval: opening a page whose address you did not type and no search returned (the address itself could carry your data to someone else's server), and saving a memory. Sending mail, adding events, controlling devices and the other actions always ask anyway. On a page approval, **Always allow** trusts that site (and its subdomains) for your profile from then on, also in scheduled tasks; the list is under Settings › Trusted sites, where you can add or remove sites. Only opening pages can be allowed for good.

**Skills.** A skill is a procedure you approved: a name, when to use it and numbered steps. When a new request shares enough words with a skill's name and description, its steps are given to the model and the use is counted. Save one from an answer with the lightning button (Leona drafts it from the conversation and you edit it), ask Leona to "remember how to do this", or write one under Settings › Skills.

Calls that cannot succeed (an edit whose text is missing or ambiguous, a file that already exists, a refused command) fail before you are asked. Commands run as your user without a sandbox: read each one before approving. Relevant memories are recalled automatically at the start of a run as untrusted data; turn memory off in Settings. Scanned PDFs need OCR, which is not included. Browser automation (Playwright) is not available yet.

## Personal: mail, calendar, home and automations

Add accounts under Settings › Accounts and turn on Personal for a message. Accounts belong to your profile (see Profiles). Several accounts of each kind are fine; each gets a name, and the tools take an `account` argument when there is more than one. Passwords and tokens are encrypted with ASP.NET Data Protection (keys in `backend/keys`, keep it out of backups you share) and can only be entered on the computer or over HTTPS.

| Account | Settings |
| --- | --- |
| Mail (IMAP/SMTP) | Loopia by default: `mailcluster.loopia.se`, IMAP 993 and SMTP 465 with SSL, the full e-mail address as user name. Other providers via Servers. |
| Calendar (CalDAV) | Apple Calendar: `https://caldav.icloud.com`, your Apple ID and an [app-specific password](https://support.apple.com/en-us/102654) (account.apple.com › Sign-In and Security). A read-only `.ics`/`webcal://` link also works. |
| Home Assistant | The address and a long-lived access token (your profile › Security). |
| Spotify | Your own app at [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) (Web API; Development Mode needs Spotify Premium and allows five users). Add the redirect URIs Leona shows (`http://127.0.0.1:5080/spotify/callback`, and `https://<computer>.ts.net/spotify/callback` with Tailscale), paste the Client ID and press Connect Spotify. The login uses PKCE, so there is no client secret; the refresh token is stored encrypted. A second person is added under User Management in the Spotify app and connects in their own profile. |

| Tool | What it does | Approval |
| --- | --- | --- |
| `mail_search`, `mail_read` | Search and read mail (all, unread or read; up to 30); reading never marks messages as seen. `mail_read` lists numbered attachments. The folder `Drafts` (or `Sent`) finds the mailbox's own folder, and drafts are shown as cards with their full text. Search results appear as a list to tick (sender, subject, date, unread and newsletter marks, "Select newsletters"); its Move to trash, Archive and Mark read buttons act directly through `POST /api/mail/manage`, the click being the approval | No |
| `mail_attachment` | Reads a PDF, Word or text attachment with page numbers, `find` and `start` (via a temporary file that is deleted right after) | No |
| `mail_manage` | Archive, delete, flag, unflag, mark read or unread, for up to 50 messages at once. Archiving creates an `Archive` folder if the mailbox has none (Loopia mailboxes start without one). Deleting moves messages to the mailbox's trash; Leona never empties it | Yes, with the messages listed |
| `find_activities` | What there is to do in Göteborg on a day: Göteborgs Stad's calendar (through the API its calendar page uses: libraries, culture houses, open preschools such as Draken) and goteborg.com's events (Liseberg, museums, concert halls). Cancelled events, ongoing ones closed that weekday and, by default, ones for children older than three are left out; activities for the youngest and those at a set time come first. Each comes with its photo; replies show suggestions as numbered entries with the photo, fetched through Leona (`/api/images`) so the sites never see the phone | No |
| `weather` | SMHI's open point forecast (snow1g) for Göteborg: temperature, rain and wind through the day | No |
| `find_jobs` | Open job ads in the Göteborg region from Platsbanken (Arbetsförmedlingen's open JobSearch API, no key), from the career pages of watched employers (Settings › Job radar; Teamtailor, Lever, Greenhouse, Ashby, Workable, Recruitee, SmartRecruiters, Varbi and Workday, also on a company's own domain; an employer without a supported page is watched by name in Platsbanken) and, with `discover`, from career pages a web search finds: several searches at once, the same role from several sources shown once, employers or single ads left out, recruitment and consulting firms skipped unless the ad is for a product company, deadlines and links. With `new_only` it reports each ad once (`personal/<profile>/jobs-seen.txt`) | No |
| `mail_draft` | Saves a draft (or a reply in the same thread) in Drafts; the chat shows it as a card exactly as saved | No |
| `mail_send` | Sends, and keeps a copy in Sent | Yes, with the full mail |
| `calendar_events` | Events in a date range, across all calendars | No |
| `calendar_create` | Adds an event | Yes |
| `calendar_update` | Changes title, time, place or notes of an event found by title and date. A date alone keeps the time of day ("move it to Friday"); the length is kept unless a new end is given. One occurrence of a repeating event gets its own copy (`RECURRENCE-ID`); `whole_series` changes title, place and notes of every occurrence but cannot move the series. Uses the ETag like deleting | Yes, showing before → after |
| `calendar_delete` | Deletes an event found by title and date (and time when several match). For a repeating event only that occurrence goes (an `EXDATE` is added to the series) unless `whole_series` is set. Uses the event's ETag, so a changed event is not deleted; read-only `.ics` links cannot be changed | Yes, with the event shown |
| `home_states`, `home_action` | Reads devices grouped by room (rooms come from Home Assistant's template endpoint; a token that may not render templates lists them without rooms). The query matches room, name and type in any inflection ("kontoret", "taklampan"); lists say "Showing 60 of N". `home_action` calls a service such as `light.turn_off` | `home_action` yes |
| `record_expense`, `list_expenses` | Receipts to the profile's own CSV (`backend/personal/<profile>/expenses.csv`, download it under Settings › Accounts) | Recording yes |
| `schedule_task`, `watch_page` | Create a schedule or a watch from a chat | Yes |
| `music_taste` | Top artists (last month, half year, years) and recently played, with genres, from Spotify | No |
| `find_concerts` | Upcoming concerts in Göteborg for an artist, or for your Spotify artists plus suggestions in your genres; `new_only` reports each concert once | No |

Automations (the clock in the sidebar) run prompts on a schedule, such as the morning brief (weekdays 07:00: today's calendar, recent mail, the weather and anything you asked Leona to remember), and watch pages for a changed value or a price below a limit. Results arrive as notifications (the bell); turn on push per device under Notifications. A scheduled run that needs approval waits and notifies you. Each task can use its own model or the default; the task card shows which, and every reply in a chat names the model that wrote it. A task that answers just "Inget nytt." (or "Nothing new.") sends no notification. A scheduled run starts fresh (earlier runs stay in its chat but are not sent to the model, so it looks again instead of answering from old results), may take up to 12 tool rounds and 24 calls, and must ask before saving an e-mail draft. `mail_search` with `full` gives the text of up to six messages that are not newsletters and of their PDF or Word attachments in one step, which suits inbox reviews; pictures from layouts and signatures are left out of attachments. Push uses VAPID keys generated on first use and needs HTTPS or localhost.

Concerts come from Göteborg's official event calendar ([goteborg.com](https://www.goteborg.com/evenemang), its public WordPress API, category *Musik & konserter*) and venue calendars read as text (`Concerts:Calendars`, Pustervik by default), refreshed every six hours. Artists are matched in code by whole words (accents ignored; tribute shows are marked), not by the model. Small venues that are on neither may be missing. **Add concert radar** in Automations runs every Monday at 09:00 with `new_only` and notifies only when there is a new concert: a scheduled run whose news-reporting tools all found nothing new ends without a notification.

People go first: a scheduled run waits until nobody has used the model for a minute (`Background:QuietSeconds`), and if someone starts chatting while it runs, its current step is stopped, thrown away and redone when the model is free. "Run now" starts immediately, and the task's card shows that it is running (with Follow along to watch it), waiting for your approval, finished, or why it could not start.

## Profiles

Each person has a profile with their own chats, accounts, automations, notifications, push devices, memories, skills, uploads, expenses and custom instructions. The database filters every query by the signed-in profile, so one profile cannot read another's data through the API or the tools.

- The computer uses the owner's profile by default; switch with the name at the bottom of the sidebar. Add, rename and remove profiles under Settings › Profiles (only on the computer). Removing a profile deletes everything in it and signs out its phones.
- A phone is paired for one profile (choose it under Settings › Phone access) and stays on it.
- Only the owner's profile gets the Files and Terminal tools, the folder settings and the shared model settings, since those act on the owner's computer. Other profiles have chat, web and Personal.

Profiles keep people's things apart in Leona; they are not protection against someone with access to the computer itself, who can read the database and files directly.

## Context and persistence

The backend allows one model inference at a time; separate conversations can have active runs while tools or approvals wait. Settings (the gear in the sidebar, stored in SQLite) control the default model (used in chats on every device, which switch over whenever it changes, and by scheduled tasks without a model of their own; empty means the model Ollama lists first, which is the newest installed), the context window (16,384 tokens by default, capped by the model's own maximum from `/api/show`), the answer length (2,048 tokens), an extra thinking budget used only when Thinking is on (4,096), how long Ollama keeps the model loaded, search result count, page excerpt size and automatic titles; these are shared and only the owner's profile changes them. Custom instructions are per profile. At least half the window always stays available for the prompt.

Each request starts with up to 16 complete history messages and four saved tool excerpts. A byte-based estimate trims older turns and halves large tool results before submission. Ollama's measured `prompt_eval_count` calibrates that estimate per model (with a safety margin), so long conversations keep more context. When a round stops at the output limit (`done_reason: length`), the reply is marked as truncated and the interface offers Continue; a reply cut off while thinking says so.

Each request allows up to four tool rounds and eight total calls. The next inference runs without tools to produce a final answer from retrieved information. Extra calls are skipped with a visible status. Thinking is off by default and collapsed when enabled. After the first exchange the model names the conversation (can be turned off in Settings).

SQLite stores conversations, replies and short tool excerpts (up to 1800 characters, arguments and source metadata); these are excerpts, not model-generated summaries. Follow-up requests recall up to four excerpts, shortened to 600 characters each. Older/full articles are not guaranteed to remain in model context. Stopped/failed replies are marked incomplete and excluded from future message history.

EF Core migrations run on startup. Back up `backend/harness.db` before updating to a version with new migrations. The database path is relative to the backend working directory unless the connection string is overridden.

## Development

```sh
dotnet build backend
dotnet run --project tests/Harness.Checks
python3 tests/run_lifecycle.py
python3 tests/evals/run_evals.py --model qwen3.5:4b
cd frontend
npm run format
npm run format:check
npm run build
```

Prettier formats the frontend; `.editorconfig` and `AGENTS.md` define code conventions. The console checks exercise tool restrictions, page windows, settings, token calibration, truncation, structured tool events, fake-model loop behavior, persistence and page encodings.

`tests/evals/run_evals.py` scores real model behavior against a running backend and Ollama: tool choice, reading pages before answering, invented links, reply language, declined actions, truncation and expected facts. Cases live in `tests/evals/cases.json`; results are written to `tests/evals/results/` and `--baseline <file>` lists checks that changed. Web cases need SearXNG and internet access (`--skip-web` skips them). Eval conversations are deleted afterwards unless `--keep` is given; files approved by eval cases stay in `workspace/evals/`.

Runs are persisted and stream over reconnectable SSE, so reloading or closing the page does not stop an answer; Stop cancels it. After a backend restart, unfinished runs are marked interrupted and actions are never replayed automatically. See [docs/agent-runs.md](docs/agent-runs.md) for the run, event and approval API.

## Security

Started with `dotnet run`, the backend listens on localhost only; the installed service turns on phone access, which opens port 5080 on your network. The computer itself needs no sign-in; phones pair with a one-time code (see Phone access) and Siri uses a key that only works for `/api/ask`. Account passwords and tokens are encrypted with ASP.NET Data Protection. Everything personal stays out of git: the database, `backend/keys`, `backend/personal`, `backend/uploads`, `backend/workspace` and `.env` are ignored. Commands run as your user without a sandbox, so read each one before approving it.

## License

MIT, see [LICENSE](LICENSE).
