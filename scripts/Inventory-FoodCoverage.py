"""Inventory all installed/reference nutrition assets; static evidence is not runtime coverage."""
import argparse
import hashlib
import json
import zipfile
from collections import Counter
from pathlib import Path
from food_assets import read_json, nutrition_fields, code_patterns

ROOT = Path(__file__).resolve().parent.parent


def inventory(folder, profiles, package_paths=None):
    packages = []
    for package in sorted(package_paths if package_paths is not None else folder.iterdir(), key=lambda p: p.name.casefold()):
        if not package.is_dir() and package.suffix.lower() != '.zip':
            continue
        if package.name.startswith(('vsslssmod-', 'vsslssmoddebug-')) or 'body physiology' in package.name.lower() or 'bodyphysiology' in package.name.lower():
            continue
        row = {'package': package.name, 'nutritionAssets': [], 'errors': []}
        archive = None
        try:
            if package.is_dir():
                entries = [p.relative_to(package).as_posix() for p in package.rglob('*.json')]
                read = lambda name: (package / name).read_bytes()
            else:
                digest = hashlib.sha256()
                with package.open('rb') as stream:
                    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                        digest.update(chunk)
                row['sha256'] = digest.hexdigest()
                archive = zipfile.ZipFile(package)
                entries, read = archive.namelist(), archive.read
            row['containsCode'] = any(n.lower().endswith('.dll') for n in (archive.namelist() if archive else
                                      [p.name for p in package.rglob('*.dll')]))
            for name in sorted(entries):
                normalized = name.replace('\\', '/')
                if package.is_dir() and package.name == 'assets':
                    normalized = 'assets/' + normalized
                if not normalized.endswith('.json') or '/assets/' not in '/' + normalized:
                    continue
                if not any('/' + kind + '/' in normalized for kind in ('itemtypes', 'blocktypes', 'patches')):
                    continue
                raw = read(name)
                text = raw.decode('utf-8-sig')
                candidate = 'nutritionprops' in text.lower()
                if not candidate:
                    continue
                try:
                    data = read_json(text)
                except (ValueError, SyntaxError) as exc:
                    row['errors'].append({'asset': normalized, 'nutritionCandidate': candidate, 'error': str(exc)})
                    continue
                fields = nutrition_fields(data)
                # Patches may carry nutrition in a path string instead of an object key.
                patch = '/patches/' in normalized
                if not fields and not (patch and candidate):
                    continue
                domain = normalized.split('assets/', 1)[1].split('/')[0]
                if domain in ('survival', 'creative'):
                    domain = 'game'
                codes, unresolved = code_patterns(data, domain) if isinstance(data, dict) and not patch else ([], True)
                exact = sorted(c for c in codes if c in profiles)
                row['nutritionAssets'].append({'asset': normalized, 'assetSha256': hashlib.sha256(raw).hexdigest(),
                    'codes': codes, 'exactProfiles': exact, 'unresolvedVariants': unresolved,
                    'route': 'patch-runtime-review' if patch else 'exact-and-native-fallback' if exact else 'native-fallback-candidate',
                    'nutrition': fields})
        except (OSError, ValueError, zipfile.BadZipFile) as exc:
            row['errors'].append({'error': str(exc)})
        finally:
            if archive:
                archive.close()
        packages.append(row)
    return packages


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mods', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--summary-output', type=Path)
    parser.add_argument('--base-game', type=Path, help='Also scan the installed game assets as a separate package')
    args = parser.parse_args()
    profiles = {code for p in json.loads((ROOT / 'assets/vsslssmod/config/foods.json').read_text())['Foods'] for code in p['Codes']}
    packages = inventory(args.mods, profiles)
    if args.base_game:
        packages += inventory(args.base_game.parent, profiles, [args.base_game])
    assets = [a for p in packages for a in p['nutritionAssets']]
    summary = {'packages': len(packages), 'packagesWithNutritionAssets': sum(bool(p['nutritionAssets']) for p in packages),
               'nutritionAssets': len(assets), 'routes': dict(Counter(a['route'] for a in assets)),
               'parseErrors': sum(len(p['errors']) for p in packages)}
    result = {'scope': 'Static item/block/patch nutrition metadata only. No patch ordering, code-generated foods, dynamic servings or enabled-mod proof. Native fallback candidates require positive runtime satiety. No claim of complete runtime coverage.',
              'summary': summary, 'packages': packages}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    if args.summary_output:
        compact = {'scope': result['scope'], 'summary': summary, 'packages': [
            {**{k: p[k] for k in ('package', 'sha256', 'containsCode', 'errors') if k in p},
             'nutritionAssets': [{k: v for k, v in a.items() if k != 'nutrition'} for a in p['nutritionAssets']]}
            for p in packages]}
        args.summary_output.parent.mkdir(parents=True, exist_ok=True)
        args.summary_output.write_text(json.dumps(compact, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(summary))
