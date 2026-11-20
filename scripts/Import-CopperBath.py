"""Import the authored Vintage Story export, grounding and splitting its three cells."""
import argparse
import copy
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('source', type=Path, help='copper-bathtub.json exported from Blockbench')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1] / 'assets/vsslssmod'
source = json.loads(args.source.read_text(encoding='utf-8-sig'))
elements = []


def flatten(nodes, offset=(0, 0, 0)):
    for node in nodes:
        if any(node.get('rotation' + axis, 0) for axis in 'XYZ'):
            raise ValueError('Rotated source elements require a mesh-aware importer')
        start = [a + b for a, b in zip(node['from'], offset)]
        if node.get('faces'):
            e = copy.deepcopy(node)
            e.pop('children', None)
            e.pop('rotationOrigin', None)
            e['from'] = start
            e['to'] = [a + b for a, b in zip(node['to'], offset)]
            elements.append(e)
        flatten(node.get('children', []), start)


flatten(source['elements'])
floor = min(e['from'][1] for e in elements)
for e in elements:
    e['from'][1] -= floor
    e['to'][1] -= floor


def shape(parts):
    return {**{key: copy.deepcopy(source[key]) for key in
               ('textureWidth', 'textureHeight', 'textureSizes', 'textures')}, 'elements': parts}


def save(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


inventory = []
for cell, code in enumerate(('copperbathtub-part', 'copperbathtub', 'copperbathtub-end')):
    parts = []
    for original in elements:
        e = copy.deepcopy(original)
        lo, hi = e['from'][0], e['to'][0]
        left = max(lo, cell * 16) if cell else lo
        right = min(hi, (cell + 1) * 16) if cell < 2 else hi
        if right <= left:
            continue
        # Preserve the authored UV density across cell cuts, including flipped faces.
        for face, data in e['faces'].items():
            if face in ('east', 'west'):
                continue
            reverse = face in ('north', 'up')
            if data.get('rotation', 0) == 180:
                reverse = not reverse
            a, b = (hi - right, hi - left) if reverse else (left - lo, right - lo)
            u0, v0, u1, v1 = data['uv']
            data['uv'] = [u0 + (u1 - u0) * a / (hi - lo), v0,
                          u0 + (u1 - u0) * b / (hi - lo), v1]
        if left > lo:
            e['faces'].pop('west', None)
        if right < hi:
            e['faces'].pop('east', None)
        e['from'][0], e['to'][0] = left - cell * 16, right - cell * 16
        parts.append(e)
        item = copy.deepcopy(e)
        for key in ('from', 'to'):
            item[key] = [(item[key][0] + cell * 16 + .5) * 16 / 49,
                         item[key][1] * 16 / 49,
                         8 + (item[key][2] - 8) * 16 / 49]
        inventory.append(item)
    save(root / f'shapes/block/{code}.json', shape(parts))
    block_path = root / f'blocktypes/{code}.json'
    block = json.loads(block_path.read_text(encoding='utf-8-sig'))
    block.pop('collisionbox', None)
    block.pop('selectionbox', None)

    def box(start, end):
        return {**{axis + '1': start[i] / 16 for i, axis in enumerate('xyz')},
                **{axis + '2': end[i] / 16 for i, axis in enumerate('xyz')}}

    block['collisionboxes'] = [box(e['from'], e['to']) for e in parts if 'band' not in e['name']]
    block['selectionboxes'] = [box([min(e['from'][i] for e in parts) for i in range(3)],
                                   [max(e['to'][i] for e in parts) for i in range(3)])]
    save(block_path, block)
save(root / 'shapes/block/copperbathtub-inventory.json', shape(inventory))
