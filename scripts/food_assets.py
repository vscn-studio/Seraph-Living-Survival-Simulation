"""Read nutrition metadata without loading mod assemblies or executing asset code."""
import ast
import itertools
import json
import re
from fnmatch import fnmatchcase

TOKENS = re.compile(r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\r\n]*|/\*[\s\S]*?\*/|[A-Za-z_$][\w$]*(?=\s*:)|,(?=\s*[}\]])')


def read_json(text):
    def replace(match):
        token = match[0]
        if token.startswith('"'):
            return token
        if token.startswith("'"):
            return json.dumps(ast.literal_eval(token), ensure_ascii=False)
        if token.startswith(('/', ',')):
            return ''
        return json.dumps(token)
    # Strip comments first so trailing commas followed by comments are recognized.
    text = TOKENS.sub(replace, text.lstrip('\ufeff'))
    return json.loads(TOKENS.sub(replace, text))


def nutrition_fields(node, path=''):
    result = {}
    if isinstance(node, dict):
        for key, value in node.items():
            child = path + '/' + key
            if 'nutritionprops' in key.lower():
                result[child] = value
            else:
                result.update(nutrition_fields(value, child))
    elif isinstance(node, list):
        for i, value in enumerate(node):
            result.update(nutrition_fields(value, path + '/' + str(i)))
    return result


def code_patterns(asset, domain):
    obj = {key.lower(): value for key, value in asset.items()}
    code = obj.get('code')
    if not isinstance(code, str):
        return [], True
    groups, unresolved = [], False
    for group in obj.get('variantgroups', []):
        group = {key.lower(): value for key, value in group.items()}
        states = group.get('states', [])
        if group.get('loadfromproperties') or not states or not all(isinstance(x, str) for x in states):
            states, unresolved = ['*'], True
        groups.append(states)
    if groups and __import__('math').prod(map(len, groups)) > 20000:
        return [f'{domain}:{code}-*'], True
    codes = [code + ''.join('-' + x for x in states if x) for states in itertools.product(*groups)]
    if not unresolved:
        codes = [c for c in codes if not any(fnmatchcase(c, x) for x in obj.get('skipvariants', []))
                 and (not obj.get('allowedvariants') or any(fnmatchcase(c, x) for x in obj['allowedvariants']))]
    return [c if ':' in c else domain + ':' + c for c in codes], unresolved
