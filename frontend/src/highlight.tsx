import type { ReactNode } from 'react';

// A small syntax highlighter for the languages Leona usually writes. It colours comments, strings,
// numbers, keywords, types and calls; anything it does not know is shown as plain text.

type Grammar = {
  name: string;
  keywords: Set<string>;
  literals: Set<string>;
  // Comment and string patterns, tried before words and numbers.
  comments: string[];
  strings: string[];
  ignoreCase?: boolean;
  // Capitalised names are types in C-like languages.
  types?: boolean;
  // Words and strings followed by a colon are keys (JSON, YAML, CSS).
  keys?: boolean;
  markup?: boolean;
};

const words = (text: string) => new Set(text.split(/\s+/).filter(Boolean));

const lineComment = (start: string) => `${start}[^\\n]*`;
const blockComment = String.raw`/\*[\s\S]*?(?:\*/|$)`;
const double = String.raw`"(?:\\.|[^"\\\n])*"?`;
const single = String.raw`'(?:\\.|[^'\\\n])*'?`;
const backtick = '`(?:\\\\.|[^`\\\\])*`?';
const tripleDouble = String.raw`"""[\s\S]*?(?:"""|$)`;

const cLike = {
  comments: [lineComment('//'), blockComment],
  strings: [double, single, backtick],
  types: true,
};

const js: Grammar = {
  name: 'JavaScript',
  ...cLike,
  keywords:
    words(`const let var function return if else for while do switch case break continue new class
    extends import from export default async await try catch finally throw typeof instanceof in of delete
    yield static get set`),
  literals: words('true false null undefined this NaN'),
};

const ts: Grammar = {
  ...js,
  name: 'TypeScript',
  keywords: new Set([
    ...js.keywords,
    ...words(`interface type enum implements public private protected readonly as keyof declare
      namespace abstract satisfies`),
  ]),
};

const csharp: Grammar = {
  name: 'C#',
  ...cLike,
  strings: [tripleDouble, String.raw`@"(?:""|[^"])*"?`, String.raw`\$?` + double, single],
  keywords:
    words(`using namespace class struct record interface enum public private protected internal
    static readonly const void var new return if else for foreach in while do switch case break continue
    try catch finally throw async await get set init override virtual abstract sealed partial base is as
    out ref params string int long bool double decimal float object char byte dynamic nameof typeof
    default where yield lock event delegate operator required when with`),
  literals: words('true false null this value'),
};

const generic: Grammar = {
  name: 'Code',
  ...cLike,
  keywords: new Set([
    ...ts.keywords,
    ...csharp.keywords,
    ...words(`func fn let mut impl trait pub mod use crate match loop struct package go defer chan map
      select range val fun when object companion data sealed throws extends final synchronized`),
  ]),
  literals: words('true false null nil None this self'),
};

const python: Grammar = {
  name: 'Python',
  comments: [lineComment('#')],
  strings: [
    tripleDouble,
    String.raw`'''[\s\S]*?(?:'''|$)`,
    String.raw`[fbr]?` + double,
    String.raw`[fbr]?` + single,
  ],
  keywords:
    words(`def return if elif else for while in not and or is import from as class try except
    finally raise with lambda pass break continue global nonlocal yield async await assert del match case`),
  literals: words('None True False self cls'),
  types: true,
};

const shell: Grammar = {
  name: 'Shell',
  comments: [lineComment('#')],
  strings: [double, single],
  keywords:
    words(`if then else elif fi for in do done while until case esac function return export local
    echo exit sudo cd set unset source alias read shift`),
  literals: words('true false'),
};

const sql: Grammar = {
  name: 'SQL',
  comments: [lineComment('--'), blockComment],
  strings: [single, double],
  ignoreCase: true,
  keywords:
    words(`select from where insert into values update set delete create table drop alter add
    column join left right inner outer full cross on group by order having limit offset as and or not
    is in like between distinct count sum avg min max primary key foreign references index unique union
    all case when then else end exists default with returning asc desc`),
  literals: words('null true false'),
};

const json: Grammar = {
  name: 'JSON',
  comments: [],
  strings: [double],
  keywords: new Set(),
  literals: words('true false null'),
  keys: true,
};

const yaml: Grammar = {
  name: 'YAML',
  comments: [lineComment('#')],
  strings: [double, single],
  keywords: new Set(),
  literals: words('true false null yes no on off'),
  keys: true,
};

const css: Grammar = {
  name: 'CSS',
  comments: [blockComment],
  strings: [double, single],
  keywords: words('important media supports keyframes import from to'),
  literals: new Set(),
  keys: true,
};

const markup: Grammar = {
  name: 'HTML',
  comments: [String.raw`<!--[\s\S]*?(?:-->|$)`],
  strings: [double, single],
  keywords: new Set(),
  literals: new Set(),
  markup: true,
};

const grammars: Record<string, Grammar> = {
  js,
  javascript: js,
  jsx: { ...js, name: 'JSX' },
  mjs: js,
  ts,
  typescript: ts,
  tsx: { ...ts, name: 'TSX' },
  cs: csharp,
  csharp,
  'c#': csharp,
  py: python,
  python,
  sh: shell,
  bash: shell,
  shell,
  zsh: shell,
  console: shell,
  sql,
  json,
  jsonc: { ...json, comments: [lineComment('//'), blockComment] },
  yaml,
  yml: yaml,
  css,
  scss: { ...css, name: 'SCSS', comments: [lineComment('//'), blockComment] },
  html: markup,
  xml: { ...markup, name: 'XML' },
  svg: { ...markup, name: 'SVG' },
  java: { ...generic, name: 'Java' },
  kotlin: { ...generic, name: 'Kotlin' },
  go: { ...generic, name: 'Go' },
  rust: { ...generic, name: 'Rust' },
  rs: { ...generic, name: 'Rust' },
  swift: { ...generic, name: 'Swift' },
  c: { ...generic, name: 'C' },
  cpp: { ...generic, name: 'C++' },
  php: { ...generic, name: 'PHP' },
};

// The label shown above a code block, such as "C#" for "csharp".
export function languageName(lang: string) {
  if (!lang) {
    return 'Text';
  }
  return grammars[lang.toLowerCase()]?.name ?? lang;
}

const patterns = new Map<Grammar, RegExp>();

function patternFor(grammar: Grammar) {
  let pattern = patterns.get(grammar);
  if (!pattern) {
    const parts = [
      grammar.comments.length ? `(?<comment>${grammar.comments.join('|')})` : '',
      grammar.markup ? String.raw`(?<tag></?[\w:\-]+|/?>)` : '',
      grammar.markup ? String.raw`(?<attr>[\w:\-]+(?==))` : '',
      `(?<string>${grammar.strings.join('|')})`,
      String.raw`(?<number>#[0-9a-fA-F]{3,8}\b|\b0x[0-9a-fA-F]+\b|\b\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?(?:px|em|rem|%|ms|s|vh|vw|fr|m|f|d|L)?\b)`,
      // Dashes belong to names only in CSS and markup; elsewhere "a-b" is a subtraction.
      grammar.keys || grammar.markup
        ? String.raw`(?<word>[\p{L}_@\-][\p{L}\p{N}_\-]*)`
        : String.raw`(?<word>[\p{L}_$@][\p{L}\p{N}_$]*)`,
    ].filter(Boolean);
    // Unicode mode lets names such as räkna_ord count as one word.
    pattern = new RegExp(parts.join('|'), 'gu');
    patterns.set(grammar, pattern);
  }
  pattern.lastIndex = 0;
  return pattern;
}

// The first character after index on the same line, skipping spaces and tabs.
function nextChar(code: string, index: number) {
  let i = index;
  while (code[i] === ' ' || code[i] === '\t') {
    i++;
  }
  return code[i];
}

// Returns the code as spans with tok-* classes, or the plain text for unknown languages.
export function highlight(code: string, lang: string): ReactNode {
  const grammar = grammars[lang.toLowerCase()];
  if (!grammar) {
    return code;
  }
  const out: ReactNode[] = [];
  let last = 0;
  for (const match of code.matchAll(patternFor(grammar))) {
    const groups = match.groups ?? {};
    const text = match[0];
    const start = match.index ?? 0;
    const after = nextChar(code, start + text.length);
    let kind: string | null = null;
    if (groups.comment) {
      kind = 'comment';
    } else if (groups.tag) {
      kind = 'keyword';
    } else if (groups.attr) {
      kind = 'key';
    } else if (groups.string) {
      kind = grammar.keys && after === ':' ? 'key' : 'string';
    } else if (groups.number) {
      kind = 'number';
    } else if (groups.word) {
      const key = grammar.ignoreCase ? text.toLowerCase() : text;
      if (grammar.keywords.has(key)) {
        kind = 'keyword';
      } else if (grammar.literals.has(key)) {
        kind = 'literal';
      } else if (grammar.keys && after === ':') {
        kind = 'key';
      } else if (after === '(') {
        kind = 'function';
      } else if (grammar.types && /^\p{Lu}\p{Ll}/u.test(text)) {
        kind = 'type';
      }
    }
    if (!kind) {
      continue;
    }
    out.push(code.slice(last, start));
    out.push(
      <span key={start} className={`tok-${kind}`}>
        {text}
      </span>,
    );
    last = start + text.length;
  }
  out.push(code.slice(last));
  return out;
}
