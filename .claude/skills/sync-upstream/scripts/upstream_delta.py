#!/usr/bin/env python3
"""List every upstream Zeroshot change between two native releases as a parity checklist, or check a filled one.

  upstream_delta.py --to <tag> [--from <tag>] [--upstream DIR] > checklist.md
  upstream_delta.py --check checklist.md [--stage plan|done]

--from defaults to the release pinned in native.props. --upstream defaults to $TMPDIR/zeroshot-upstream, cloned
blobless on first use and fetched afterwards. Never point it at a checkout someone works in.

Rows describe the SDK at its merge-base with origin/main, so the bump's own edits never change the expected rows.
"""
import argparse
import functools
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(subprocess.run(['git', 'rev-parse', '--show-toplevel'], check=True, capture_output=True, text=True).stdout.strip())
UPSTREAM_URL = 'https://github.com/the-open-engine/zeroshot.git'
PROTOCOL = 'protocol/openengine-cluster/v1'
SDK_SCHEMAS = ['contracts', 'oecp', 'compiled-ir']
CLASSES = {'no-change', 'additive', 'breaking', 'not-bound'}
NEEDS_TEST = {'additive', 'breaking'}
HEADER = re.compile(r'<!-- upstream-delta from=(\S+) to=(\S+) -->')
CITED = re.compile(r'the-open-engine/zeroshot/(?:blob|tree)/[0-9a-f]{40}/([^)#"\s>]+)')
BUCKETS = [('protocol', 'protocol/'), ('protocol-crate', 'crates/openengine-cluster-protocol/'),
           ('server-crate', 'crates/openengine-cluster-server/'), ('client-crate', 'crates/openengine-cluster-client/'),
           ('zeroshot/src', 'zeroshot/src/'), ('ui', 'ui/'), ('sdks', 'sdks/'), ('distribution', 'distribution/')]


def git(up, *args):
    return subprocess.run(['git', '-C', str(up), *args], check=True, capture_output=True, text=True).stdout


def show(up, rev, path):
    r = subprocess.run(['git', '-C', str(up), 'show', f'{rev}:{path}'], capture_output=True)
    return r.stdout if r.returncode == 0 else None


def prepare(up, *revs):
    if not (up / '.git').exists():
        subprocess.run(['git', 'clone', '--quiet', '--filter=blob:none', '--no-checkout', UPSTREAM_URL, str(up)], check=True)
    elif any(subprocess.run(['git', '-C', str(up), 'rev-parse', '--verify', '--quiet', f'{rev}^{{commit}}'],
                            capture_output=True).returncode for rev in revs):
        git(up, 'fetch', '--quiet', '--tags', '--force', 'origin')


def pinned():
    props = (ROOT / 'native.props').read_text()
    version = re.search(r'<ZeroshotNativeVersion>([^<]+)<', props).group(1)
    revision = re.search(r'<ZeroshotNativeSourceRevision>([^<]+)<', props).group(1)
    return f'v{version}', revision


def canon(value):
    return json.dumps(value, sort_keys=True)


def upstream_defs(up, rev):
    defs = {}
    for path in git(up, 'ls-tree', '-r', '--name-only', rev, PROTOCOL).split():
        if not path.endswith('.json') or '/fixtures/' in path or '/goldens/' in path:
            continue
        doc = json.loads(show(up, rev, path))
        if not isinstance(doc, dict):
            continue
        found = {**doc.get('$defs', {}), **doc.get('definitions', {}), **doc.get('components', {}).get('schemas', {})}
        for name, schema in found.items():
            defs.setdefault(name, {})[path] = schema
    return defs


def refs(schema):
    if isinstance(schema, dict):
        for key, value in schema.items():
            if key == '$ref' and isinstance(value, str):
                if '#/' in value:
                    yield value.rsplit('/', 1)[-1]
            else:
                yield from refs(value)
    elif isinstance(schema, list):
        for item in schema:
            yield from refs(item)


def describe(old, new):
    notes = [keyed('properties', merged(old, 'properties'), merged(new, 'properties')),
             keyed('required', dict.fromkeys(merged(old, 'required')), dict.fromkeys(merged(new, 'required')))]
    for key in ('oneOf', 'anyOf'):
        variants = [dict(variant(v) for v in schema.get(key) or []) for schema in (old, new)]
        notes.append(keyed(f'{key} variants', *variants, detail=True))
    rules = sorted(k for k in old.keys() | new.keys()
                   if k not in {'properties', 'required', 'oneOf', 'anyOf', 'allOf'} and canon(old.get(k)) != canon(new.get(k)))
    if canon([rule(s) for s in old.get('allOf', [])]) != canon([rule(s) for s in new.get('allOf', [])]):
        rules.append('allOf rules')
    notes.append(' '.join(f'~{k}' for k in rules))
    return '; '.join(filter(None, notes)) or 'shape changed'


def merged(schema, key):
    parts = [schema, *schema.get('allOf', [])]
    if key == 'required':
        return [k for part in parts for k in part.get('required', [])]
    return {k: v for part in parts for k, v in part.get('properties', {}).items()}


def rule(schema):
    return {k: v for k, v in schema.items() if k not in {'properties', 'required'}}


def keyed(label, old, new, detail=False):
    marks = [f'+{k}' for k in new if k not in old] + [f'-{k}' for k in old if k not in new]
    marks += [f'~{k}' + (f' ({describe(old[k], new[k])})' if detail else '')
              for k in old if k in new and canon(old[k]) != canon(new[k])]
    return f'{label} ' + ' '.join(marks) if marks else ''


def variant(schema):
    for name, prop in schema.get('properties', {}).items():
        if 'const' in prop:
            return f'{name}={prop["const"]}', schema
    return schema.get('title') or schema.get('$ref', '').rsplit('/', 1)[-1] or canon(schema)[:40], schema


def cell(text):
    return text.replace('|', '\\|').replace('\n', ' ')


def rows(up, old, new):
    out = []
    changed_files = set(git(up, 'diff', '--name-only', old, new).split())

    log = git(up, 'log', '--reverse', '--format=%x00%h%x09%s', '--name-only', f'{old}..{new}')
    for entry in filter(None, log.split('\x00')):
        head, *files = [line for line in entry.splitlines() if line]
        sha, subject = head.split('\t', 1)
        touched = [label for label, prefix in BUCKETS if any(f.startswith(prefix) for f in files)]
        out.append(('commits', f'C:{sha}', f'{subject} [{", ".join(touched) or "other"}]'))

    before, after = upstream_defs(up, old), upstream_defs(up, new)
    closure = {}
    for name in SDK_SCHEMAS:
        for key, schema in json.loads(at_base(f'src/Zeroshot.Sdk/Schemas/{name}.schema.json'))['$defs'].items():
            closure.setdefault(key, []).append(name)
    for key, files in sorted(closure.items()):
        where = '+'.join(files)
        if key not in before:
            continue
        if key not in after:
            out.append(('schema', f'S:{key}', f'{where}: removed upstream'))
        elif canon(before[key]) != canon(after[key]):
            out.append(('schema', f'S:{key}', f'{where}: {describe(*(next(iter(d[key].values())) for d in (before, after)))}'))

    def reached(defs):
        seen, todo = set(), [key for key in closure if key in defs]
        while todo:
            for schema in defs[todo.pop()].values():
                for target in set(refs(schema)) - seen:
                    seen.add(target)
                    if target in defs:
                        todo.append(target)
        return seen
    for key in sorted(reached(after) - reached(before) - closure.keys()):
        out.append(('schema', f'R:{key}', 'referenced from the SDK closure but absent from the SDK schemas'))
    for key in sorted(after.keys() - before.keys() - reached(after)):
        out.append(('schema', f'N:{key}', 'new upstream definition in ' + ', '.join(sorted(Path(p).name for p in after[key]))))

    def methods(rev):
        doc = json.loads(show(up, rev, f'{PROTOCOL}/openrpc.json') or b'{"methods": []}')
        return {m['name']: canon(m) for m in doc['methods']}
    old_methods, new_methods = methods(old), methods(new)
    for name in sorted(old_methods.keys() | new_methods.keys()):
        if name not in new_methods:
            out.append(('methods', f'M:{name}', 'removed'))
        elif name not in old_methods:
            out.append(('methods', f'M:{name}', 'added'))
        elif old_methods[name] != new_methods[name]:
            out.append(('methods', f'M:{name}', 'params or result changed'))

    copies = [('src/Zeroshot.Sdk/Schemas/compiled-ir.schema.json', f'{PROTOCOL}/compiled-ir.schema.json'),
              ('src/Zeroshot.Sdk/Schemas/NATIVE-LICENSE', 'LICENSE')]
    names = {Path(p).name: p for p in git(up, 'ls-tree', '-r', '--name-only', old, 'protocol', 'crates').split()}
    for fixture in sorted(git(ROOT, 'ls-tree', '--name-only', base(), 'tests/Zeroshot.Sdk.Tests/Fixtures/cluster/').split()):
        if Path(fixture).name in names:
            copies.append((fixture, names[Path(fixture).name]))
    for ours, theirs in copies:
        if theirs in changed_files:
            state = 'removed upstream' if show(up, new, theirs) is None else 'changed upstream'
            out.append(('copies', f'F:{ours}', f'{state}: {theirs}'))

    sources = [f for f in git(ROOT, 'ls-tree', '-r', '--name-only', base()).split()
               if Path(f).suffix in {'.md', '.json', '.cs', '.sh', '.ps1', '.py', '.props', '.txt'}]
    cited = sorted({m.group(1) for path in sources for m in CITED.finditer(at_base(path))})
    for path in cited:
        if path in changed_files:
            state = 'removed' if show(up, new, path) is None else 'changed'
            out.append(('cited', f'P:{path}', f'cited upstream source {state}'))
    return out


@functools.cache
def base():
    return git(ROOT, 'merge-base', 'HEAD', 'origin/main').strip()


def at_base(path):
    return (show(ROOT, base(), path) or b'').decode(errors='replace')


SECTIONS = {
    'commits': 'Upstream commits. Each touched area is in brackets; read the diff for every commit that touches protocol, a crate, zeroshot/src or sdks.',
    'schema': 'Schema definitions. S: a definition the SDK copies changed. R: the SDK closure now references a definition it lacks. N: a definition new upstream.',
    'methods': 'OECP methods in openrpc.json.',
    'copies': 'Files the SDK copies from upstream.',
    'cited': 'Upstream sources the SDK docs cite as evidence.',
}


def emit(up, old, new):
    found = rows(up, old, new)
    sha_old, sha_new = git(up, 'rev-parse', f'{old}^{{commit}}').strip(), git(up, 'rev-parse', f'{new}^{{commit}}').strip()
    releases = [t for t in git(up, 'tag', '--list', 'v*', '--contains', old, '--merged', new).split() if t != old]
    print(f'<!-- upstream-delta from={old} to={new} -->')
    print(f'# Upstream parity {old} -> {new}\n')
    print(f'- From `{old}` at `{sha_old}`; to `{new}` at `{sha_new}`.')
    print(f'- Releases in range: {", ".join(f"`{t}`" for t in releases) or "none"}.')
    print('- Class is one of ' + ', '.join(f'`{c}`' for c in sorted(CLASSES)) + '. `additive` and `breaking` rows name their test.\n')
    for section, intro in SECTIONS.items():
        print(f'## {section.capitalize()}\n\n{intro}\n')
        print('| ID | Upstream change | Class | SDK handling |\n| --- | --- | --- | --- |')
        for _, key, text in (r for r in found if r[0] == section):
            print(f'| {cell(key)} | {cell(text)} | TODO | TODO |')
        print()


def check(path, stage, up):
    text = Path(path).read_text()
    header = HEADER.search(text)
    if not header:
        sys.exit(f'{path}: missing the upstream-delta header')
    prepare(up, *header.groups())
    expected = {key for _, key, _ in rows(up, *header.groups())}
    seen, problems = set(), []
    changed = set(git(ROOT, 'diff', '--name-only', base()).split() + git(ROOT, 'ls-files', '--others', '--exclude-standard').split())
    for line in text.splitlines():
        cells = [c.strip() for c in re.split(r'(?<!\\)\|', line)[1:-1]]
        if len(cells) != 4 or not re.match(r'^[CSRNMFP]:', cells[0]):
            continue
        key, _, klass, handling = cells
        seen.add(key)
        if klass not in CLASSES:
            problems.append(f'{key}: class "{klass}" is not one of {sorted(CLASSES)}')
        if not handling or handling == 'TODO':
            problems.append(f'{key}: no SDK handling')
        if klass in NEEDS_TEST:
            tests = re.findall(r'tests/[\w./-]+\.cs', handling)
            if not tests:
                problems.append(f'{key}: {klass} row names no tests/... .cs file')
            elif stage == 'done':
                problems.extend(f'{key}: {t} is unchanged since origin/main' for t in tests if t not in changed)
    problems.extend(f'{key}: missing from the checklist' for key in sorted(expected - seen))
    problems.extend(f'{key}: not an upstream change in this range' for key in sorted(seen - expected))
    for problem in problems:
        print(problem)
    print(f'{len(seen)} rows, {len(problems)} problems')
    sys.exit(1 if problems else 0)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--from', dest='old')
    parser.add_argument('--to', dest='new')
    parser.add_argument('--upstream', type=Path, default=Path(os.environ.get('TMPDIR', tempfile.gettempdir())) / 'zeroshot-upstream')
    parser.add_argument('--check', metavar='CHECKLIST')
    parser.add_argument('--stage', choices=['plan', 'done'], default='done')
    args = parser.parse_args()
    if args.check:
        check(args.check, args.stage, args.upstream)
    if not args.new:
        parser.error('--to is required')
    tag, revision = pinned()
    old = args.old or tag
    prepare(args.upstream, old, args.new)
    if not args.old and git(args.upstream, 'rev-parse', f'{tag}^{{commit}}').strip() != revision:
        sys.exit(f'{tag} does not resolve to the pinned source revision {revision}; pass --from explicitly')
    emit(args.upstream, old, args.new)


if __name__ == '__main__':
    main()
