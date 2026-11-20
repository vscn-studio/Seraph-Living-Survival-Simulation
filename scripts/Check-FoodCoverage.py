"""Fail unreviewed registered foods; classify release portions against fresh game registries.
Run once per isolated pack. A registry check does not replace dynamic ingestion tests.
"""
import argparse, collections, json
from pathlib import Path

p=argparse.ArgumentParser(description=__doc__);p.add_argument("reports",nargs="+",type=Path)
p.add_argument("--output",type=Path)
p.add_argument("--require-resolved",action="store_true",help="Also require fresh runtime intake to resolve all six reserves")
a=p.parse_args()
c=json.loads((Path(__file__).resolve().parents[1]/"assets/vsslssmod/config/foods.json").read_text(encoding="utf-8"))
results=[];failures=[]
micro_keys={'calciumMg','ironMg','zincMg','vitaminAUgRAE','vitaminCMg','b1Mg','b6Mg','folateUgDFE','b12Ug'}
def matches(code, pattern):
 return code.startswith(pattern[:-1]) if pattern.endswith('*') else code==pattern
def profile_for(code):
 # Mirror FoodCatalog.Find: exact codes win, then longest prefix, then catalog order.
 candidates=[(len(pattern)-1 if pattern.endswith('*') else 10000+len(pattern),-i,f)
  for i,f in enumerate(c['Foods']) for pattern in f['Codes'] if matches(code,pattern)]
 return max(candidates,key=lambda row:row[:2])[2] if candidates else None
for path in a.reports:
 report=json.loads(path.read_text(encoding="utf-8-sig"));rows=[]
 for row in report["Foods"]:
  code=row["Code"];profile=profile_for(code)
  excluded=next((v for k,v in c["Exclusions"].items() if matches(code,k)),None)
  if row.get("Error"): route="unhandled";reason=row["Error"];failures.append(code)
  elif excluded: route="excluded";reason=excluded
  elif row["Route"]=="actual-ingredients-at-consumption": route="dynamic";reason="Actual retained stacks; per-serving intake tested separately"
  elif profile and profile['Coverage']!='fallback' and profile['PortionG']>0 and micro_keys<=profile['MicroPer100G'].keys():
   route=profile["Coverage"];reason=profile["PortionBasis"]
   if a.require_resolved and (row.get('ResolvedMassG',0)<=0 or set(row.get('KnownReserves',[]))!={'calcium','iron','zinc','a','c','bcomplex'}):
    route='unhandled';reason='Runtime intake did not resolve mass and all six nutrient reserves';failures.append(code)
  else: route="unhandled";reason=row.get("Error","Missing explicit portion or complete micronutrients");failures.append(code)
  rows.append({"Code":code,"Route":route,"Reason":reason})
 result={"Registry":str(path),"Counts":dict(collections.Counter(r["Route"] for r in rows)),"Foods":rows};results.append(result)
 print(str(path)+": "+json.dumps(result["Counts"]))
if a.output:a.output.write_text(json.dumps(results,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
if failures:raise SystemExit("FAIL unhandled registered foods: "+", ".join(sorted(set(failures))))
print("PASS all registered foods have explicit portions and nine physical micronutrients; no category fallback counted as coverage; dynamic dishes tested separately")
