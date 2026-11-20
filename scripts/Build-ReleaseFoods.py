"""Build release edible portions from pinned USDA SR Legacy and reviewed game conversions.
No physiology-mod data are read. Source and portion evidence are emitted into foods.json.
"""
import argparse, csv, hashlib, io, json, zipfile
from pathlib import Path
from food_processing import RETENTION
from pack_food_profiles import extend_pack

SHA256 = "79705f75a7306d0910ad16dd4d66a5caf602dad528b99b77c7a510bfaf3d6e14"
MICRO = {"calciumMg":"301", "ironMg":"303", "zincMg":"309", "vitaminAUgRAE":"320", "vitaminCMg":"401",
         "b1Mg":"404", "b6Mg":"415", "folateUgDFE":"435", "b12Ug":"418"}
SEEDS = [('09003', ['game:fruit-redapple', 'game:fruit-pinkapple', 'game:fruit-yellowapple'], 'raw', 1), ('09050', ['game:fruit-blueberry'], 'raw', 1), ('09042', ['game:fruit-blackberry'], 'raw', 1), ('09070', ['game:fruit-cherry'], 'raw', 1), ('09078', ['game:fruit-cranberry'], 'raw', 1), ('09084', ['game:fruit-redcurrant', 'game:fruit-whitecurrant'], 'raw', 1), ('09176', ['game:fruit-mango'], 'raw', 1), ('09200', ['game:fruit-orange'], 'raw', 1), ('09236', ['game:fruit-peach'], 'raw', 1), ('09252', ['game:fruit-pear'], 'raw', 1), ('09266', ['game:fruit-pineapple'], 'raw', 1), ('09286', ['game:fruit-pomegranate'], 'raw', 1), ('09302', ['game:fruit-raspberry'], 'raw', 1), ('09316', ['game:fruit-strawberry'], 'raw', 1), ('11124', ['game:vegetable-carrot', 'expandedfoods:choppedvegetable-carrot'], 'raw', 1), ('11109', ['game:vegetable-cabbage', 'expandedfoods:choppedvegetable-cabbage'], 'raw', 1), ('11282', ['game:vegetable-onion', 'expandedfoods:choppedvegetable-onion'], 'raw', 1), ('11298', ['game:vegetable-parsnip', 'expandedfoods:choppedvegetable-parsnip'], 'raw', 1), ('11422', ['game:vegetable-pumpkin', 'expandedfoods:choppedvegetable-pumpkin'], 'raw', 1), ('11564', ['game:vegetable-turnip', 'expandedfoods:choppedvegetable-turnip'], 'raw', 1), ('12220', ['game:grain-flax'], 'raw', 1), ('12036', ['game:grain-sunflower'], 'raw', 1), ('20062', ['game:grain-rye'], 'raw', 1), ('20001', ['game:grain-amaranth'], 'raw', 1), ('01124', ['aculinaryartillery:eggwhiteportion'], 'raw', 1.03), ('01125', ['aculinaryartillery:eggyolkportion-chicken'], 'raw', 1.03), ('01123', ['aculinaryartillery:eggyolkfullportion-chicken'], 'raw', 1.03), ('04053', ['expandedfoods:foodoilportion-olive'], 'as-served', 0.91), ('20140', ['game:grain-spelt'], 'raw', 1), ('20444', ['game:grain-rice'], 'raw', 1), ('20031', ['game:grain-millet'], 'raw', 1), ('11134', ['game:vegetable-cassava'], 'raw', 1), ('16108', ['game:vegetable-soybean', 'game:legume-soybean'], 'raw', 1), ('16087', ['game:vegetable-peanut', 'game:legume-peanut'], 'raw', 1), ('11352', ['pemmican:vegetable-potato-raw', 'pemmican:choppedvegetable-potato'], 'raw', 1), ('11167', ['pemmican:vegetable-corn-raw', 'pemmican:choppedvegetable-corn'], 'raw', 1), ('01212', ['pemmican:longmilk-dry'], 'dried', 1), ('19335', ['pemmican:sugar'], 'dry', 1), ('04518', ['pemmican:foodoilportion-corn'], 'as-served', 0.91), ('15072', ['primitivesurvival:fisheggs-raw-normal', 'primitivesurvival:fisheggs-raw-ovulated'], 'raw', 1), ('15207', ['primitivesurvival:fisheggscooked'], 'cooked', 1)]
# Public edible household measures in WEIGHT.txt; a game portion of loose grain/berries
# uses the named household serving, whereas whole fruit/roots use a named whole item.
MEASURES = {
 "09003":("medium (3",182), "09050":("50 berries",68), "09042":("cup",144),
 "09070":("cherry",8.2), "09078":("cup, whole",100), "09084":("cup",112),
 "09176":("fruit without refuse",336), "09200":("fruit (2",131), "09236":("medium",150),
 "09252":("medium",178), "09266":("fruit",905), "09286":("pomegranate",282),
 "09302":("10 raspberries",19), "09316":("medium",12), "11124":("medium",61),
 "11109":("head, medium",908), "11282":("medium",110), "11298":("cup slices",133),
 "11422":("cup (1",116), "11564":("medium",122), "12220":("cup, whole",168),
 "12036":("cup",140), "20062":("cup",169), "20001":("cup",193),
 "20140":("cup",174), "20444":("cup",185), "20031":("cup",200),
 "11134":("root",408), "16108":("cup",186), "16087":("cup",146),
 "11352":("Potato medium",213), "11167":("ear, medium",102), "01212":("cup",128),
 "19335":("serving packet",2.8), "15072":("3 oz",85), "15207":("3 oz",85),
 "01123":("large",50), "01124":("large",33), "01125":("large",17),
 "04053":("tablespoon",13.5), "04518":("tbsp",13.6)
}

def main():
 ap=argparse.ArgumentParser(description=__doc__);ap.add_argument("archive",type=Path);ap.add_argument("--check",action="store_true")
 args=ap.parse_args();raw=args.archive.read_bytes()
 if hashlib.sha256(raw).hexdigest()!=SHA256: raise ValueError("USDA archive SHA256 mismatch")
 with zipfile.ZipFile(io.BytesIO(raw)) as z:
  def rows(name): return csv.reader(io.StringIO(z.read(name).decode("cp1252")),delimiter="^",quotechar="~")
  data={};desc={r[0]:r[2] for r in rows("FOOD_DES.txt")};weights={}
  for r in rows("NUT_DATA.txt"): data.setdefault(r[0],{})[r[1]]=float(r[2])
  for r in rows("WEIGHT.txt"): weights.setdefault(r[0],[]).append((r[2],r[3],float(r[4])))
 foods=[]
 def add(ndb,codes,mass,basis,prep="raw",coverage="reference",density=1):
  d=data[ndb]
  # Missing observations are never silently turned into zero. These are explicit,
  # nutrient-specific food substitutions, propagated into recipe provenance.
  donors={"09083":{"folateUgDFE":"09084"}, "09546":{"vitaminAUgRAE":"09063","folateUgDFE":"09063"},
          "18024":{"vitaminAUgRAE":"18023"}}
  micros={k:d[v] for k,v in MICRO.items() if v in d}
  substitutions=[]
  for key,donor in donors.get(ndb,{}).items():
   if key not in micros:
    micros[key]=data[donor][MICRO[key]];substitutions.append(key+" from NDB "+donor)
  if set(micros)!=set(MICRO):raise ValueError("Incomplete source record "+ndb+": "+str(set(MICRO)-set(micros)))
  p={"Codes":codes,"Source":"USDA SR Legacy NDB "+ndb+"; "+desc[ndb],"Confidence":"medium",
   "Preparation":prep,"Coverage":coverage,"PortionMethod":"explicit","PortionG":mass,"PortionBasis":basis,
   "Density":density,"WaterGPer100G":d.get("255",0),"EnergyKJPer100G":d.get("208",0)*4.184,
   "SodiumMgPer100G":d.get("307",0),"PotassiumMgPer100G":d.get("306",0),
   "Per100G":{"carbohydrate":max(0,d.get("205",0)-d.get("291",0)),"protein":d.get("203",0),"fat":d.get("204",0),"fiber":d.get("291",0)},
   "MicroPer100G":micros}
  if substitutions:p["PortionBasis"]+="; nutrient substitutions: "+", ".join(substitutions)
  if d.get("221",0)>0:p["AlcoholGPer100G"]=d["221"]
  foods.append(p);return p
 def find(code): return next(p for p in foods if code in p["Codes"])
 def clone(code,parent,scale,basis,prep=None):
  p=json.loads(json.dumps(find(parent)));p["Codes"]=[code];p["PortionG"]*=scale;p["PortionBasis"]=basis;p["Coverage"]="recipe"
  if "CookedReference" in p:p["CookedQuantityScale"]=p.get("CookedQuantityScale",1)*scale
  if prep:p["Preparation"]=prep
  foods.append(p);return p
 for ndb,codes,prep,density in SEEDS:
  label,mass=MEASURES[ndb]
  if not any(abs(g-mass)<1e-6 for _,_,g in weights[ndb]): raise ValueError("Unverified USDA serving "+ndb)
  # Expanded Foods chopped outputs depend on its own recipe variant, never share a whole vegetable mass by alias.
  codes=[c for c in codes if not c.startswith("expandedfoods:choppedvegetable-")]
  if not codes:continue
  basis=f"USDA WEIGHT NDB {ndb}: {label}, {mass:g} g edible; one named serving per game item"
  if any("portion" in c for c in codes): basis="Actual consumed mL x configured food density; component amount uses itemsPerLitre; density g/mL="+str(density)
  # Loose harvested grain is a 50 g portion; legumes and berries are 100 g.
  # These are explicit game item units, not USDA household measure claims.
  if any(c.startswith('game:grain-') for c in codes): mass=50;basis='Seraph harvested grain portion: 50 g dry grain'
  if any(c.endswith('-soybean') or c.endswith('-peanut') for c in codes): mass=100;basis='Seraph harvested legume portion: 100 g dry edible seed'
  if ndb in ['09050','09042','09070','09078','09084','09302','09316']: mass=100;basis='Seraph loose fruit portion: 100 g edible berries or cherries'
  if ndb=='11109': mass=300;basis='Seraph cabbage portion: 300 g edible leaves'
  if ndb=='19335': mass=100;basis='Seraph refined sugar portion: 100 g'
  add(ndb,codes,mass,basis,prep,density=density)
 # A whole pineapple cuts into twelve game fruit portions (game grid pineappleslice recipe).
 pineapple=find("game:fruit-pineapple");pineapple["PortionG"]=905/12;pineapple["Coverage"]="recipe"
 pineapple["PortionBasis"]="USDA NDB 09266 whole edible fruit 905 g / game grid/pineappleslice output 12"
 add("09266",["game:pineapple"],905,"USDA WEIGHT: one edible pineapple fruit 905 g")
 add("01123",["game:egg-chicken-raw"],50,"USDA WEIGHT NDB 01123: one large egg, edible 50 g")
 add("01211",["game:milkportion"],10.3,"Actual mL x 1.03 g/mL milk density; USDA unfortified whole milk",density=1.03)
 for ndb,code in [("01145","unsalted"),("01001","salted")]:
  add(ndb,["game:butter-"+code],113,"USDA WEIGHT: one stick 113 g",prep="churned")
 for ndb,kind in [("01009","cheddar"),("01004","blue")]:
  for slices in range(1,5):
   add(ndb,[f"game:cheese-{kind}-{slices}slice"],28.35*slices,
    f"USDA WEIGHT: 1 oz edible slice 28.35 g x {slices}; game cheese cuts preserve four quarters",prep="aged",coverage="substitution")
 add("13335",["game:fat"],113,"USDA WEIGHT: 4 oz raw suet 113 g; bovine adipose reference",coverage="substitution")
 # Rendering removes moisture and connective tissue, preserving the measured fat in one input.
 fat=add("04001",["game:fat-rendered"],113*data["13335"]["204"]/100,
  "game fat rendering 1:1; output tallow mass equals input NDB 13335 lipid mass; nonfat rendering loss excluded",prep="rendered",coverage="recipe",density=.9)
 clone("hideracks:fat-chunk","game:fat-rendered",.25,"Immersive Hideworking 1.0.1 grid/cut-fat: 1 rendered fat -> 4 chunks; recombine-fat: 4 -> 1",prep="rendered")
 # Matched USDA raw/cooked yields; species-unknown game meat is explicitly a substitution.
 meats=[("redmeat","23567","23568",250,250*77/113), ("bushmeat","23567","23568",250,250*77/113),
        ("poultry","05011","05013",197,146), ("fish","15015","15016",231,180)]
 for kind,rawid,cookedid,rawg,cookedg in meats:
  rawp=add(rawid,["game:"+kind+"-raw"],rawg,f"Seraph edible meat portion {rawg:g} g; USDA NDB {rawid} composition",coverage="substitution")
  cookedp=add(cookedid,["game:"+kind+"-cooked"],cookedg,f"USDA matched yield NDB {rawid}/{cookedid}: {rawg:g} g raw -> {cookedg:g} g cooked; game 1:1",prep="cooked",coverage="substitution")
  for section in ['Per100G','MicroPer100G']:
   cookedp[section]={k:v*rawg/cookedg*(RETENTION[k] if section=='MicroPer100G' else 1) for k,v in rawp[section].items()}
  for key in ['SodiumMgPer100G','PotassiumMgPer100G']:
   cookedp[key]=rawp[key]*rawg/cookedg
  cookedp['WaterGPer100G']=100-(100-rawp['WaterGPer100G'])*rawg/cookedg
  cookedp['PortionBasis']+='; edible dry matter conserved, one heat-retention stage'
  rawp["CookedReference"]=cookedp["Codes"][0]
  if kind in ["redmeat","bushmeat","fish"]:
   clone("primitivesurvival:jerky-"+kind+"-raw",rawp["Codes"][0],.25,
    "Primitive Survival 5.1.4 grid/jerky-"+kind+": four raw strips per game meat input")
   clone("primitivesurvival:jerky-"+kind+"-cooked",cookedp["Codes"][0],.25,
    "Primitive Survival strips 4 per meat; matched USDA cooked edible yield divided by 4",prep="cooked")
  for domain,code in [("primitivesurvival","fishfillet") ] if kind=="fish" else []:
   for state in ["raw","cooked"]:clone(domain+":"+code+"-"+state,"game:fish-"+state,1,"Fish fillet substitution using declared cod fillet serving",prep=state)
 for state in ["raw","cooked"]:
  clone("butchering:primemeat-"+state,"game:redmeat-"+state,1,"Prime meat species/cut unknown; declared beef reference serving",prep=state)["Coverage"]="substitution"
 add("15139",["primitivesurvival:crabmeat-raw"],21,"USDA WEIGHT: edible yield of one blue crab 21 g",coverage="substitution")
 add("15140",["primitivesurvival:crabmeat-cooked"],21,"Blue crab cooked reference, same edible serving; species substitution",prep="cooked",coverage="substitution")
 # Cooked references retain measured micronutrients instead of blanking every cooked ingredient.
 for rawcode,ndb in [("carrot","11125"),("cabbage","11110"),("onion","11283"),("parsnip","11299"),("pumpkin","11423"),("turnip","11565")]:
  parent=find("game:vegetable-"+rawcode)
  cooked=add(ndb,["reference:cooked-"+rawcode],parent["PortionG"],"Cooked USDA reference at parent edible serving mass; no measured game yield, explicit substitution",prep="cooked",coverage="substitution")
  parent["CookedReference"]=cooked["Codes"][0]
 for veg,ndb in [("potato","11367"),("corn","11168")]:
  parent=find("pemmican:vegetable-"+veg+"-raw")
  p=add(ndb,["pemmican:cookedchoppedvegetable-"+veg+"-perfect"],parent["PortionG"],"Long-term food 0.7.3 1:1 vegetable chop; cooked USDA serving substitution",prep="cooked",coverage="substitution")
  parent["CookedReference"]=p["Codes"][0]
 # Grain milling is 1:1. Dough explicitly contains the litre consumed by game grid recipes.
 for grain in ["flax","sunflower","rye","amaranth","spelt","rice","millet"]:
  rawp=find("game:grain-"+grain)
  flour=clone("game:flour-"+grain,"game:grain-"+grain,1,"Game quern: 1 grain -> 1 whole flour; same edible mass",prep="milled")
  dough=json.loads(json.dumps(flour));dough["Codes"]=["game:dough-"+grain];dough["Preparation"]="dough"
  mass=flour["PortionG"];dough["PortionG"]=mass+1000;dough["PortionBasis"]="Game grid/dough: 1 flour + 1 litre water -> 1 dough; ingredient masses summed, no loss"
  dough["WaterGPer100G"]=(mass*flour["WaterGPer100G"]/100+1000)/(mass+1000)*100
  for section in ["Per100G","MicroPer100G"]:dough[section]={k:v*mass/(mass+1000) for k,v in flour[section].items()}
  for key in ['SodiumMgPer100G','PotassiumMgPer100G']:dough[key]=flour[key]*mass/(mass+1000)
  foods.append(dough)
 # Baking/dehydration: a measured final moisture reference determines water loss.
 # Nonvolatile dry ingredients are conserved; vitamins use explicit processing
 # retention coefficients. These are model parameters, not measured game yields.
 def dehydrate(code,parent,moisture,reference,basis,coverage="substitution"):
  original=find(parent);p=json.loads(json.dumps(original));p["Codes"]=[code]
  dry=original["PortionG"]*(1-original["WaterGPer100G"]/100)
  mass=dry/(1-moisture/100)
  if mass>original["PortionG"]+1e-8: raise ValueError("Drying cannot add water: "+code)
  ratio=original["PortionG"]/mass;p["PortionG"]=mass;p["WaterGPer100G"]=moisture
  p["Per100G"]={k:v*ratio for k,v in original["Per100G"].items()}
  heat=original["Preparation"] in ["raw","dough","prepared","as-served"]
  p["MicroPer100G"]={k:v*ratio*(RETENTION[k] if heat else 1) for k,v in original["MicroPer100G"].items()}
  for key in ["SodiumMgPer100G","PotassiumMgPer100G"]:p[key]=original[key]*ratio
  p["Source"]=original["Source"].split(";")[0]+"; final moisture reference USDA NDB "+reference
  p["PortionBasis"]=basis+"; dry matter and sodium/potassium conserved; Seraph retention on first heating (food_processing.py)"
  p["Preparation"]="dried";p["Coverage"]=coverage;p.pop("CookedReference",None);foods.append(p);return p
 for grain in ["flax","sunflower","rye","amaranth","spelt","rice","millet"]:
  bread=dehydrate("game:bread-"+grain+"-perfect","game:dough-"+grain,data["18077"]["255"],"18077",
   "Game oven 1 dough -> 1 bread; whole-wheat bread moisture surrogate for this grain")
  # Rusk recipe is 1 loaf -> 1 rusk item, not one thin slice.
  clone("pemmican:rusks-"+grain+"-raw",bread["Codes"][0],1,"Long-term food 0.7.3 grid/rusks: 1 loaf -> 1 raw rusk",prep="baked")
  dehydrate("pemmican:rusks-"+grain+"-dry",bread["Codes"][0],data["18224"]["255"],"18224",
   "Long-term food 1:1 drying; measured rusk moisture reference")
 for kind in ["redmeat","bushmeat","fish"]:
  cooked="game:"+kind+"-cooked";ref="15077" if kind=="fish" else "19002"
  for domain,code in [("game",kind+"-smoked"),("game",kind+"-cured-smoked"),
    ("primitivesurvival","smokedmeat-"+kind+"-cooked"),
    ("primitivesurvival","curedsmokedmeat-"+kind+"-cooked"),
    ("butchering","smoked-none-"+kind),("butchering","smoked-cured-"+kind)]:
   dehydrate(domain+":"+code,cooked,data[ref]["255"],ref,
    "Game/mod one meat -> one smoked meat; declared cooked yield then moisture-reference drying")
  dehydrate("primitivesurvival:jerky-"+kind+"-cured","primitivesurvival:jerky-"+kind+"-cooked",
   data[ref]["255"],ref,"Four strips per meat; declared cooked yield then moisture-reference drying")
 # Pack-specific edible families. Exact recipes are used where the pack exposes them;
 # named source records are completed with explicit pack portions by extend_pack
 # before serialization. No unfinished serving or missing micro is released.
 def sourced(ndb, codes, basis, prep="as-served", portion=0, coverage="substitution"):
  p=add(ndb,codes,portion,basis,prep,coverage)
  if portion==0:p["PortionMethod"]="satiety-energy"
  return p
 def sourced_family(ndb, codes, basis, prep="as-served"):
  return sourced(ndb,codes,basis,prep,0,"substitution")
 sourced_family("23568",["butchering:primemeat-*"],
  "USDA cooked beef substitution; prime-cut class/healing variant portion follows native satiety",prep="cooked")
 # Use a documented household serving or a stated species substitution, consistently
 # with the existing release catalog. Do not turn low-energy fruit into kilograms
 # of food just to reproduce native satiety.
 for ndb,code,mass,note in [
  ("09059","game:fruit-breadfruit",220,"USDA breadfruit: one cup serving, 220 g"),
  ("09164","game:fruit-lychee",9.6,"USDA litchi: one fruit without refuse, 9.6 g"),
  ("09287","game:fruit-saguaro",103,"USDA prickly pear: one fruit without refuse, 103 g; cactus species substitution"),
  ("09083","game:fruit-blackcurrant",112,"USDA black currant: one cup, 112 g"),
  ("09302","game:fruit-cloudberry",19,"USDA raspberry substitution; same 10-berry portion as game raspberry"),
  ("09050","game:fruit-beautyberry",68,"USDA blueberry substitution; same 50-berry portion as game blueberry")]:
  sourced(ndb,[code],note,prep="raw",portion=mass)
 # Mushroom species share the white-mushroom analogue. Native poison effects
 # remain independent of their edible mass and nutrient content.
 mushroom=sourced("11260",["game:mushroom-*"],"USDA white mushroom: one cup pieces, 70 g; edible species substitution",prep="raw",portion=70)
 cooked_mushroom=sourced("11261",["reference:cooked-mushroom"],"USDA cooked white mushroom composition at one 70 g game serving; cooking yield substitution",prep="cooked",portion=70)
 mushroom["CookedReference"]="reference:cooked-mushroom"
 # Pickling is 1:1 in game. Preserve the source serving and composition rather than
 # increasing nutrients by assigning a larger mass to the pickled item.
 pickle_sources={"carrot":"11124","cabbage":"11109","onion":"11282","parsnip":"11298",
  "pumpkin":"11422","turnip":"11564","bellpepper":"11821","olive":"09193","fennel":"11957"}
 extra_vegetables={"bellpepper":119,"olive":15,"fennel":234}
 for veg,mass in extra_vegetables.items():
  sourced(pickle_sources[veg],["game:vegetable-"+veg],"USDA household serving; pepper medium 119 g, olives five large 15 g, fennel bulb 234 g",prep="raw",portion=mass)
 for veg,ndb in pickle_sources.items():
  p=clone("game:pickledvegetable-"+veg,"game:vegetable-"+veg,1,
   "Game 1:1 pickling; source vegetable composition substitution; brine uptake unknown",prep="pickled")
  p["Coverage"]="substitution"
 # Source compositions for fish chunks and preserved-meat families. The pack
 # extension assigns explicit size-dependent servings and overrides exact variants.
 sourced_family("15016",["game:fishchunk-*"],"USDA cooked Atlantic cod; fish species/fillet-size substitution",prep="cooked")
 for code in ["butchering:smoked-none-primemeat","butchering:smoked-healing-primemeat"]:
  if not any(code in f["Codes"] for f in foods):sourced_family("19002",[code],"USDA dried beef substitution; cured/smoked prime cut yield unknown",prep="smoked")
 for kind,ndb in [("redmeat","19002"),("bushmeat","19002"),("fish","15077"),("poultry","05013")]:
  sourced_family(ndb,["primitivesurvival:smokedmeat-"+kind+"-*","primitivesurvival:curedsmokedmeat-"+kind+"-*"],
   "USDA species-specific preserved/cooked meat substitution; native satiety serving for unmeasured states",prep="smoked")
 clone("game:redmeat-vintage","game:redmeat-cooked",1,"Aged beef uses cooked beef serving; aging loss unmeasured",prep="aged")["Coverage"]="substitution"
 clone("pemmican:redmeat-vintageraw","game:redmeat-raw",1,"Long-term Food raw aged beef reference serving",prep="raw")["Coverage"]="substitution"
 # Blood sausage/pudding have a direct SR Legacy entry. Its composition is the
 # base family reference; exact variants below use blood/meat/casing recipes.
 sourced_family("07005",["butchering:sausage-bloodsausage-*","butchering:sausage-blackpudding-*"],
  "USDA blood sausage; recipe variant serving scaled by native satiety",prep="cooked")
 # Initial staple references; extend_pack replaces these with complete flour,
 # water and potato recipes and supplies all edible baking states.
 for state in ["perfect"]:
  sourced_family("11134",["game:bread-cassava-"+state],"USDA raw cassava staple substitution; baked item energy scaled to native satiety",prep="baked")
 sourced_family("18024",["pemmican:bread-corn-perfect"],"USDA prepared cornbread substitution at native serving energy; recipe differs",prep="baked")
 sourced_family("18971",["pemmican:bread-potato-perfect"],"USDA potato bread substitution at native serving energy",prep="baked")
 # Long-term food staples with identifiable ingredient families.
 sourced_family("19411",["pemmican:chips-*"],"USDA salted potato chips; Long-term Food prepared potato serving")
 sourced_family("11363",["pemmican:cookedvegetable-potato-*","pemmican:cookedvegetable-pickledpotato-*","pemmican:cookedchoppedvegetable-pickledpotato-*"],"USDA baked potato; cooked/pickled potato serving",prep="cooked")
 sourced_family("11168",["pemmican:cookedvegetable-corn-*"],"USDA boiled sweet corn; cooked corn serving",prep="cooked")
 sourced_family("11363",["pemmican:roastvegetable-potato-*"],"USDA baked potato flesh; roasted potato serving",prep="cooked")
 sourced_family("11168",["pemmican:roastvegetable-corn-*"],"USDA cooked sweet corn; roasted corn serving",prep="cooked")
 for code in ["pemmican:mushroomclean-raw","pemmican:mushroomclean-pickled","pemmican:mushroompate-raw"]:
  clone(code,"game:mushroom-*",1,"Long-term Food washing/grinding/pickling 1:1; wash water discarded",prep="pickled" if code.endswith("pickled") else "raw")
 dried_mushroom=dehydrate("pemmican:mushroompate-dry","pemmican:mushroompate-raw",data["11268"]["255"],"11268",
  "Long-term Food 1:1 baking; dried shiitake moisture substitution")
 clone("pemmican:kebab-mushroom","reference:cooked-mushroom",2,"Long-term Food skewer: two mushroom servings",prep="cooked")
 # Primitive Survival fish roe profiles also cover cured caviar; snake meat uses
 # species-unknown game meat references with state-specific preparation.
 sourced_family("15012",["primitivesurvival:caviar-*"],"USDA granular caviar; native satiety serving",prep="cured")
 for state,base in [("raw","game:bushmeat-raw"),("partbaked","game:bushmeat-cooked"),("cooked","game:bushmeat-cooked"),("charred","game:bushmeat-cooked")]:
  clone("primitivesurvival:snakemeat-"+state,base,1,"Primitive Survival snake meat; species-unknown game meat substitution",prep=state)["Coverage"]="substitution"
 def composite(codes, recipe_parts, basis, prep="as-served", coverage="recipe"):
  if any(p["PortionG"]<=0 or qty<=0 for p,qty in recipe_parts):
   raise ValueError("Composite ingredients need positive explicit masses: "+str(codes))
  total=sum(p["PortionG"]*qty for p,qty in recipe_parts)
  result={"Codes":codes,"Source":"USDA SR Legacy; recipe: "+basis,"Confidence":"medium",
   "Preparation":prep,"Coverage":coverage,"PortionBasis":basis,"PortionG":total,
   "PortionMethod":"explicit","Density":1,
   "WaterGPer100G":sum(p["PortionG"]*qty*p["WaterGPer100G"] for p,qty in recipe_parts)/total,
   "EnergyKJPer100G":sum(p["PortionG"]*qty*p["EnergyKJPer100G"] for p,qty in recipe_parts)/total,
   "SodiumMgPer100G":sum(p["PortionG"]*qty*p.get("SodiumMgPer100G",0) for p,qty in recipe_parts)/total,
   "PotassiumMgPer100G":sum(p["PortionG"]*qty*p.get("PotassiumMgPer100G",0) for p,qty in recipe_parts)/total,
   "Per100G":{},"MicroPer100G":{}}
  for key in sorted(set.intersection(*(set(p["Per100G"]) for p,_ in recipe_parts)) - {"electrolytes"}):
   result["Per100G"][key]=sum(p["PortionG"]*qty*p["Per100G"].get(key,0) for p,qty in recipe_parts)/total
  for key in sorted(set.intersection(*(set(p["MicroPer100G"]) for p,_ in recipe_parts))):
   result["MicroPer100G"][key]=sum(p["PortionG"]*qty*p["MicroPer100G"].get(key,0) for p,qty in recipe_parts)/total
  # Preserve the physical source record. Reserve yield follows nutrient mass,
  # so dilution, splitting and combining cannot bypass blood-iron balancing.
  scales={}
  for key in sorted(set().union(*(set(p.get('MicroReserveScale',{})) for p,_ in recipe_parts))):
   physical=sum(p['PortionG']*qty*p['MicroPer100G'].get(key,0) for p,qty in recipe_parts)
   effective=sum(p['PortionG']*qty*p['MicroPer100G'].get(key,0)*p.get('MicroReserveScale',{}).get(key,1) for p,qty in recipe_parts)
   scales[key]=effective/physical if physical else 1
  if scales:result['MicroReserveScale']=scales
  foods.append(result);return result
 # One nutrition bar uses one peanut, two grain servings, 0.1 L honey and salt.
 # Average the recipe's allowed grains because the finished item stores no selected
 # ingredient identity; salt quantity is not a nutrient source for this catalog.
 honey=add("19296",["reference:bar-honey"],142,"Long-term Food nutribar recipe: 0.1 L honey at USDA honey density 1.42 g/mL",coverage="recipe")
 grain_profiles=[next(f for f in foods if "game:grain-"+g in f["Codes"]) for g in ["flax","sunflower","rye","amaranth","spelt","rice","millet"]]
 peanut=next(f for f in foods if "game:legume-peanut" in f["Codes"])
 recipe_parts=[(p,2/len(grain_profiles)) for p in grain_profiles]+[(peanut,1),(honey,1)]
 composite(["pemmican:nutribar-general","pemmican:nutribar-healing"],recipe_parts,
  "Long-term Food 0.7.3 nutribar: one peanut + two averaged allowed grain servings + 0.1 L honey; salt contributes no modeled nutrients")
 # Caramel's 3x3 crafting recipe uses four fruit items and 0.25 L honey.
 fruit_codes=["game:fruit-redapple","game:fruit-blackberry","game:fruit-blueberry","game:fruit-cherry",
  "game:fruit-cranberry","game:fruit-redcurrant","game:fruit-mango","game:fruit-orange","game:fruit-peach",
  "game:fruit-pear","game:fruit-pineapple","game:fruit-pomegranate","game:fruit-raspberry","game:fruit-strawberry",
  "game:fruit-breadfruit","game:fruit-lychee","game:fruit-saguaro","game:fruit-blackcurrant","game:fruit-beautyberry","game:fruit-cloudberry"]
 fruit_profiles=[]
 for code in fruit_codes:
  profile=next((f for f in foods if code in f["Codes"]),None)
  if profile and profile not in fruit_profiles:fruit_profiles.append(profile)
 honey_25cl=json.loads(json.dumps(honey));honey_25cl["PortionG"]*=2.5
 caramel=composite(["pemmican:caramel-raw"],[(f,4/len(fruit_profiles)) for f in fruit_profiles]+[(honey_25cl,1)],
  "Long-term Food caramel: four averaged fruit servings + 0.25 L honey; output has no fruit identity",prep="raw",coverage="substitution")
 dehydrate("pemmican:caramel-dry","pemmican:caramel-raw",data["19014"]["255"],"19014",
  "Long-term Food fruit leather; 1:1 drying with fruit leather moisture reference")
 clone("pemmican:caramel-cooked","pemmican:caramel-dry",1,"Long-term Food caramel: 1 dried fruit leather -> 1 cooked sweet; dry mass conserved",prep="cooked")
 # These compact foods include fat and plants; a pure meat/mushroom surrogate
 # would discard their actual major ingredients and inflate the water fraction.
 veg_parts=[(find("game:vegetable-"+v),1/3) for v in ["onion","carrot","parsnip"]]
 composite(["pemmican:mushroompatebar"],[(dried_mushroom,5),(find("game:fat"),1)]+veg_parts,
  "Long-term Food mushroom pate: five dried mushroom portions + one fat + one averaged vegetable; dry recipe reference for shared output",prep="prepared",coverage="substitution")
 pemmican=composite(["pemmican:pemmican-raw"],[(find("game:bushmeat-cooked"),4),(find("game:fat"),1)]+[(f,3/len(fruit_profiles)) for f in fruit_profiles],
  "Long-term Food pemmican: four meat + one fat + three averaged fruit servings; cooked meat substitution",prep="prepared",coverage="substitution")
 dehydrate("pemmican:pemmican-bar","pemmican:pemmican-raw",data["19002"]["255"],"19002",
  "Long-term Food 1:1 pemmican drying; beef jerky moisture substitution")
 for state in ["cooked","salt"]:
  clone("pemmican:pemmican-"+state,"pemmican:pemmican-bar",1,"Long-term Food 1:1 preserved pemmican; retain dried recipe nutrient dose",prep=state)
 # Skewers combine one meat item with one mushroom; codes do not retain selected species.
 meat_profiles=[next(f for f in foods if c in f["Codes"]) for c in ["game:redmeat-cooked","game:bushmeat-cooked","game:poultry-cooked","game:fish-cooked"]]
 composite(["pemmican:kebab-cooked"],[(p,1/len(meat_profiles)) for p in meat_profiles]+[(cooked_mushroom,1)],
  "Long-term Food cooked kebab: one meat + one mushroom; mushroom recipe substitution for shared vegetable/skewer output",
  prep="cooked",coverage="substitution")
 # Liquid quantities come from actual mL, not native satiety. Prefer unsweetened,
 # unfortified juice records; fruit-only references are explicitly substitutions.
 juice_map={"apple":"09016","blueberry":"09050","cranberry":"09078","redcurrant":"09084","whitecurrant":"09084",
  "blackcurrant":"09083","saguaro":"09287","pineapple":"09273","cherry":"09546","peach":"09236","pear":"09252",
  "orange":"09206","mango":"09176","breadfruit":"09059","lychee":"09164","pomegranate":"09442",
  "beautyberry":"09050","blackberry":"09043","cloudberry":"09302","raspberry":"09302","strawberry":"09316"}
 for fruit,ndb in juice_map.items():
  sourced(ndb,["game:juiceportion-"+fruit],"Actual mL at 1 g/mL; USDA unfortified juice or fruit composition substitution where no juice record exists",prep="juice",portion=10)
 # Rustic Cookery 1.2.0 fixes SlabSize to six; SlabSlicing pieces are handled at consumption.
 for kind in ["redmeat","bushmeat"]:
  for state in ["raw","smoked"]:
   clone("rusticcookery:meatslab-"+kind+"-"+state,"game:"+kind+"-"+state,6,
    "Rustic Cookery 1.2.0 SlabSplitter/SlabSlicing: 6 ordinary meat pieces per whole slab; runtime pieces attribute scales partial slabs",prep=state)
 # Milk powder uses the actual one-litre recipe, not an unrelated cup of powder.
 powder=find("pemmican:longmilk-dry");milk=find("game:milkportion")
 powder["PortionG"]=1030*(1-milk["WaterGPer100G"]/100)/(1-powder["WaterGPer100G"]/100)
 powder["PortionBasis"]="Long-term food 0.7.3 cooking/milk-dry: 1 L milk -> 1 powder; milk density 1.03 g/mL, dry-solids yield and USDA powder moisture; added salt quantity unverified"
 powder["Coverage"]="substitution"
 extend_pack(foods, add, clone, find, dehydrate, composite, data)
 catalog={"Format":"seraph.release.foods","Version":1,"Foods":foods,
 "DataSource":{"Dataset":"USDA SR Legacy April 2018","ArchiveSha256":SHA256,
 "ArchiveUrl":"https://www.ars.usda.gov/ARSUserFiles/80400525/Data/SR-Legacy/SR-Leg_ASC.zip",
 "Basis":"per 100 g edible portion; WEIGHT.txt public household servings; game recipe counts",
 "Energy":"available carbohydrate/protein/fat 4/4/9 kcal/g",
 "MicroUnits":"Ca/Fe/Zn/C/B1/B6 mg; A ug RAE; folate ug DFE; B12 ug",
 "DailyReferences":"Ca 1000, Fe 12, Zn 11, A 900, C 90, B1 1.2, B6 1.7, folate 400, B12 2.4; 22 points per reference"},
 "CategoryBaselines":{},"Exclusions":{
 "envelopes:waxstick-*":"Wax seal supply, not food",
 "bodyphys:seasalt":"Mineral seasoning with no macronutrient energy; native satiety is not a composition reference",
 "em:causticpotashportion":"Caustic chemical", "em:causticsodaportion":"Caustic chemical",
 "em:cobaltsulfateportion":"Industrial metal salt", "em:mercuryportion":"Industrial mercury",
 "em:washingsodaportion":"Cleaning chemical", "game:acidportion":"Industrial acid", "game:acid-full-*":"Industrial acid container",
 "game:waterportion":"Plain water uses the hydration adapter, not food composition",
 "game:cheese-waxedcheddar-4slice":"Waxed whole cheese is not directly edible; remove wax before the edible cheese profile applies",
 },
 "ReviewedFallbacks":{}}
 for category,ndb in {"Fruit":"09003","Vegetable":"11124","Grain":"20444","Protein":"01123","Dairy":"01077"}.items():
  p=add(ndb,["reference:ndb-"+ndb],1,"Unidentified portion: 1 native satiety = 1 kcal",coverage="fallback")
  foods.remove(p);p["MicroPer100G"]={};p["PortionG"]=0;p["PortionMethod"]="satiety-energy";p["Confidence"]="low";catalog["CategoryBaselines"][category]=p
 catalog["DataSource"]["Processing"]="Seraph explicit retention model: Ca/Fe 1, A .8, C .5, B1 .7, B6 .8, folate .7, B12 .8; applied once per heating stage"
 catalog["DataSource"]["Completeness"]="All released food profiles contain nine physical micronutrients; recipe/species substitutions are recorded per profile; no reviewed category fallbacks"
 for profile in foods:
  # Electrolytes are derived from measured sodium and potassium, never stored
  # as a second synthetic macro field. Remove obsolete zero-valued entries.
  profile["Per100G"].pop("electrolytes",None)
  if set(profile["MicroPer100G"])!=set(MICRO):raise ValueError("Incomplete food: "+str(profile["Codes"]))
 path=Path(__file__).resolve().parents[1]/"assets/vsslssmod/config/foods.json"
 output=json.dumps(catalog,ensure_ascii=False,indent=2)+"\n"
 if args.check:
  if path.read_text(encoding="utf-8")!=output:raise ValueError("Food catalog differs from pinned generator")
 else:path.write_text(output,encoding="utf-8")
 print(f"PASS pinned USDA source, {len(foods)} portions and {len(catalog['ReviewedFallbacks'])} reviewed fallback families")
if __name__=="__main__":main()
