"""Check calibrated nutrient budgets against the installed Long-term Food recipe assets."""
import argparse
import json
import re
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('mods', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
catalog = json.loads((root/'assets/vsslssmod/config/foods.json').read_text(encoding='utf-8'))
def food(code):
    return next(p for p in catalog['Foods'] if code in p['Codes'])
def amount(code, key, mass=None):
    p=food(code)
    return (p['PortionG'] if mass is None else mass)*p.get('Per100G',{}).get(key,p['MicroPer100G'].get(key,0))/100
def near(a,b):
    assert abs(a-b)<1e-7,(a,b)
def load_mod(z,name):
    text=z.read(name).decode('utf-8-sig')
    text=re.sub(r'//[^\r\n]*','',text)
    return json.loads(re.sub(r',\s*([}\]])',r'\1',text))

with zipfile.ZipFile(args.mods/'Long-term food-0.7.3.zip') as z:
    recipes=load_mod(z,'assets/pemmican/recipes/cooking/syrup.json')
    patches=json.loads((root/'assets/vsslssmod/patches/food-calibration.json').read_text())
    for patch in patches:
        index=int(patch['path'].split('/')[1])
        recipes[index]['cooksInto']['quantity']=patch['value']
    for r in recipes:
        ingredient=r['ingredients'][0]
        litres=ingredient['minQuantity']*ingredient['portionSizeLitres']
        source=ingredient['validStacks'][0]['code'];output=r['cooksInto']
        source_carbs=amount(source,'carbohydrate',litres*1000*food(source)['Density'])
        if output['code']=='pemmican:sugar':
            result=amount(output['code'],'carbohydrate')*output['quantity']
            assert 0 < result <= source_carbs,(r['code'],result,source_carbs)
        else:
            result=amount(output['code'],'carbohydrate',output['quantity']/100*1000*food(output['code'])['Density'])
            near(source_carbs,result)
    starch=load_mod(z,'assets/pemmican/recipes/barrel/starch.json')
    assert starch[0]['ingredients'][1]['quantity']==1 and starch[0]['output']['stackSize']==1
    assert starch[1]['ingredients'][1]['quantity']==1 and starch[1]['output']['litres']==5
    assert amount('pemmican:starch-dry','carbohydrate')<=amount('pemmican:sugargrind-potato','carbohydrate')
    near(amount('pemmican:starch-dry','carbohydrate'),amount('pemmican:starchwaterportion','carbohydrate',5000))
    dissolve=next(r for r in load_mod(z,'assets/pemmican/recipes/barrel/wort-brewing.json') if r['code']=='syrup-sugar')
    assert dissolve['ingredients'][1]['quantity']==1 and dissolve['output']['litres']==1
    near(amount('pemmican:sugar','carbohydrate'),amount('pemmican:syrup-sugar','carbohydrate',1040))

for code in ['game:fruit-cherry','game:fruit-redapple','game:fruit-orange','game:fruit-pineapple','game:fruit-strawberry']:
    juice='game:juiceportion-'+('apple' if code.endswith('redapple') else code.split('-')[-1])
    mass=food(code)['PortionG']
    for key in food(code)['MicroPer100G'].keys() | food(code)['Per100G'].keys():
        assert amount(juice,key,mass*.7)<=amount(code,key)+1e-8,(juice,key)
print('PASS installed recipes: all syrup batch sizes, refined sugar, dissolution, potato starch, starch water, juice budgets')
