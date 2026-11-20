"""Reviewed food identities for the installed 1.22.7 pack.

Use named food records, actual recipe counts, or explicit species/composition
models. No native food-category substitution is used here. Serving weights for
unweighed items are declared game portions, not purported laboratory measures.
"""
import copy
from food_processing import RETENTION


def extend_pack(foods, add, clone, find, dehydrate, composite, data):
    def remove(code):
        for p in foods[:]:
            if code in p['Codes']:
                p['Codes'].remove(code)
                if not p['Codes']: foods.remove(p)

    def record(ndb, codes, mass, basis, prep='raw', density=1):
        for code in codes: remove(code)
        return add(ndb, codes, mass, basis, prep, 'substitution', density)

    def alias(code, parent, scale=1, prep=None, basis=None):
        remove(code)
        return clone(code, parent, scale, basis or 'Same edible ingredient and declared portion as '+parent, prep)

    def mix(code, parts, basis, prep='prepared', output=1):
        remove(code)
        p=composite([code], parts, basis, prep)
        p['PortionG']=sum(f['PortionG']*q for f,q in parts)/output
        p['PortionMethod']='explicit'
        return p

    def heated(code, parent, prep='cooked'):
        p=alias(code,parent,prep=prep)
        p.pop('CookedReference',None)
        p['MicroPer100G']={k:v*RETENTION[k] for k,v in p['MicroPer100G'].items()}
        p['PortionBasis']+='; one heating stage, Seraph retention model'
        return p

    def liquid(ndb, codes, density=1, prep='as-served'):
        return record(ndb,codes,100*density,
            f'Actual consumed volume; density {density:g} g/mL; named liquid composition substitution',prep,density)

    def dilute(code, parent, fraction, density=1):
        p=alias(code,parent,prep='liquid')
        p['Density']=density;p['PortionG']=100*density
        p['WaterGPer100G']=100-(100-p['WaterGPer100G'])*fraction
        for section in ['Per100G','MicroPer100G']:
            p[section]={k:v*fraction for k,v in p[section].items()}
        p['SodiumMgPer100G']*=fraction;p['PotassiumMgPer100G']*=fraction
        p['EnergyKJPer100G']*=fraction
        p['AlcoholGPer100G']=p.get('AlcoholGPer100G',0)*fraction
        p['PortionBasis']=f'Actual volume at {density:g} g/mL; {fraction:g} mass fraction of {parent}, balance water; explicit dilution model'
        return p

    mix('reference:rustic-roux-flour',[(find('game:flour-'+grain),1/6) for grain in
        ['spelt','rye','flax','rice','amaranth','sunflower']],
        'Rustic Cookery one flour per dish; average whole-grain flour for earlier bowls without retained ingredient identity; six default servings','milled')

    record('11026',['game:vegetable-bambooshoot'],120,'USDA bamboo shoots; declared 120 g shoot serving')
    record('11027',['reference:cooked-bambooshoot'],120,'Cooked bamboo shoots at the same edible serving','cooked')
    find('game:vegetable-bambooshoot')['CookedReference']='reference:cooked-bambooshoot'
    raw_root=record('11254',['game:cattailroot','game:papyrusroot'],100,'Raw lotus rhizome composition analogue; 100 g edible wetland root')
    raw_root['CookedReference']='game:vegetable-cookedcattailroot'
    record('11255',['game:vegetable-cookedcattailroot','game:vegetable-cookedpapyrusroot'],100,
           'Cooked lotus rhizome substitution for starchy wetland roots; 100 g edible root','cooked')
    record('12155',['game:treeseed-walnut'],4,'USDA walnut kernels; one edible nut, two halves, 4 g')
    alias('cottageindustry:motleyapple','game:fruit-redapple')
    # The animal species are absent from SR Legacy. A named invertebrate analogue
    # is explicit rather than treating these foods as eggs via the Protein category.
    record('15149',['game:insect-grub','game:insect-termite','game:insect-termite-stick'],10,
           'Raw shrimp invertebrate composition analogue; 10 g edible insect serving, stick excluded')
    record('15149',['primitivesurvival:nightcrawler'],3,
           'Raw shrimp invertebrate composition analogue; 3 g edible worm serving')
    alias('game:pickledlegume-soybean','game:legume-soybean',prep='pickled')
    def curing_salt(p, raw_mass):
        # Retained salt is modeled at 2% of the original meat mass, not 2% sodium.
        # Concentrate that same salt allowance after smoking; retain native K/Na.
        p['SodiumMgPer100G']+=raw_mass*.02*1000*.393*100/p['PortionG']
        p['Per100G'].pop('electrolytes',None)
        p.pop('CookedReference',None)
        p['PortionBasis']+='; retained salt model: 2 g NaCl per 100 g original meat, 39.3% sodium'
        return p

    for kind in ['redmeat','bushmeat','poultry','fish']:
        raw_mass=find('game:'+kind+'-raw')['PortionG']
        p=alias('game:'+kind+'-cured','game:'+kind+'-raw',prep='cured')
        curing_salt(p,raw_mass)
        # Meals must use a salted cooked reference, not unsalted meat or raw vitamins.
        cooked=alias('reference:cooked-cured-'+kind,'game:'+kind+'-cooked',prep='cooked')
        curing_salt(cooked,raw_mass)
        p['CookedReference']=cooked['Codes'][0]
        if kind!='poultry':
            for code in ['game:'+kind+'-cured-smoked','butchering:smoked-cured-'+kind]:
                curing_salt(find(code),raw_mass)
            curing_salt(find('primitivesurvival:jerky-'+kind+'-cured'),raw_mass/4)
        for family in ['smokedmeat','curedsmokedmeat']:
            for state in ['raw','partbaked','cooked','charred']:
                parent='game:'+kind+'-smoked' if kind!='poultry' else 'game:poultry-cooked'
                p=alias(f'primitivesurvival:{family}-{kind}-{state}',parent,prep='smoked')
                if family=='curedsmokedmeat':curing_salt(p,raw_mass)
    for family in ['fishfillet','crabmeat']:
        for state in ['partbaked','charred']:
            alias(f'primitivesurvival:{family}-{state}',f'primitivesurvival:{family}-cooked',prep=state)

    # Prime-cut variants consume one full meat unit; smoking removes water only.
    alias('butchering:primemeat-*','butchering:primemeat-cooked',prep='cooked')
    alias('butchering:primemeat-healing','butchering:primemeat-cooked',prep='cooked')
    curing_salt(alias('butchering:primemeat-curedhealing','butchering:primemeat-healing',prep='cured'),
                find('butchering:primemeat-raw')['PortionG'])
    for code in ['butchering:smoked-none-primemeat','butchering:smoked-healing-primemeat']:
        remove(code)
        dehydrate(code,'butchering:primemeat-cooked',data['19002']['255'],'19002',
                  'One prime meat per smoked prime meat; cooked edible dry matter conserved')
        if code=='butchering:smoked-healing-primemeat':
            curing_salt(find(code),find('butchering:primemeat-raw')['PortionG'])

    # Whole cassava -> soaked -> dried -> flour is a material chain, not raw
    # cassava nutritional density assigned to a dry loaf.
    alias('game:rawcassava-raw','game:vegetable-cassava')
    soaked=heated('game:rawcassava-soaked','game:vegetable-cassava',prep='soaked')
    dehydrate('game:rawcassava-dried',soaked['Codes'][0],12,'20068',
              'Cassava drying; 12 percent final moisture model; one root per output')
    alias('game:flour-cassava','game:rawcassava-dried',prep='milled')
    # Water is an ingredient with known zero nutrient content, not a missing record.
    water=copy.deepcopy(find('game:milkportion'))
    water.update(PortionG=1000, WaterGPer100G=100, EnergyKJPer100G=0, SodiumMgPer100G=0, PotassiumMgPer100G=0)
    for section in ['Per100G','MicroPer100G']:water[section]={k:0 for k in water[section]}
    mix('game:dough-cassava',[(find('game:flour-cassava'),1),(water,1)],
        'Game dough: one cassava flour plus 1 L water','dough')
    remove('game:bread-cassava-perfect')
    dehydrate('game:bread-cassava-perfect','game:dough-cassava',data['18077']['255'],'18077',
              'One cassava dough per loaf; bread moisture reference')
    for grain in ['spelt','rye','flax','rice','cassava','amaranth','sunflower','millet']:
        for state in ['partbaked','charred']:
            p=alias(f'game:bread-{grain}-{state}',f'game:bread-{grain}-perfect',prep=state)
            if state=='charred':p['MicroPer100G']={k:v*RETENTION[k] for k,v in p['MicroPer100G'].items()}

    beet=record('11080',['pemmican:vegetable-sugarbeet-raw'],136,'Sugar beet root model: beet micronutrients, 18 percent available carbohydrate')
    beet['Per100G']['carbohydrate']=18;beet['WaterGPer100G']=77
    for veg in ['potato','corn']:
        parent=f'pemmican:vegetable-{veg}-raw'
        for state in ['partbaked','charred']:
            heated(f'pemmican:vegetable-{veg}-{state}',parent,state)
            heated(f'pemmican:cookedchoppedvegetable-{veg}-{state}',parent,state)
        alias(f'pemmican:choppedvegetable-pickled{veg}',parent,prep='pickled')
        alias(f'pemmican:pickledvegetable-pickled{veg}',parent,prep='pickled')
    for grain,ndb,mass in [('corn','20019',102),('potato','11413',100)]:
        flour=record(ndb,[f'reference:flour-{grain}'],mass,'Named flour composition; declared dry flour serving','milled')
        if grain=='corn': alias('pemmican:flour-corn','reference:flour-corn',prep='milled')
        parts=[(flour,1),(water,1)]
        basis='One corn flour portion + 1 L water'
        if grain=='potato':
            # The pack recipe adds potato to flour; it does not mill potato into
            # the whole loaf. Its output loses the selected flour identity.
            grains=['spelt','rye','flax','rice','amaranth','sunflower']
            parts=[(find('game:flour-'+g),1/len(grains)) for g in grains]+[(find('pemmican:choppedvegetable-potato'),1),(water,1)]
            basis='One averaged whole-grain flour + one chopped potato + 1 L water; output does not retain flour identity'
        mix(f'pemmican:dough-{grain}',parts,basis,'dough')
        remove(f'pemmican:bread-{grain}-perfect')
        dehydrate(f'pemmican:bread-{grain}-perfect',f'pemmican:dough-{grain}',data['18077']['255'],'18077',
                  'One dough per loaf; bread moisture reference')
        for state in ['partbaked','charred']:
            alias(f'pemmican:bread-{grain}-{state}',f'pemmican:bread-{grain}-perfect',prep=state)
    for grain,domain in [('cassava','game'),('corn','pemmican'),('potato','pemmican')]:
        bread=f'{domain}:bread-{grain}-perfect'
        alias(f'pemmican:rusks-{grain}-raw',bread)
        dehydrate(f'pemmican:rusks-{grain}-dry',bread,data['18224']['255'],'18224','One loaf per dried rusk')
    # One 140 g potato grind cannot produce 100 g starch: recover 80% of its carbohydrate.
    starch_mass=140*find('pemmican:vegetable-potato-raw')['Per100G']['carbohydrate']/data['20027']['205']*.8
    record('20027',['pemmican:starch-dry'],starch_mass,'Starch extraction: 80 percent of carbohydrate in 140 g potato grind','dried')
    starch=alias('pemmican:starch-raw','pemmican:starch-dry')
    starch['PortionG']=starch_mass*2;starch['WaterGPer100G']=50+starch['WaterGPer100G']/2
    for section in ['Per100G','MicroPer100G']:starch[section]={k:v/2 for k,v in starch[section].items()}
    starch['PortionBasis']='Wet starch: extracted starch plus equal retained water'
    dilute('pemmican:starchwaterportion','pemmican:starch-dry',starch_mass/5000)

    liquid('01012',['game:cottagecheeseportion'],1.03)
    liquid('19296',['game:honeyportion'],1.42)
    record('19296',['game:honeycomb'],142,'Game squeezing: honey in comb yields 0.1 L; edible honey mass 142 g, wax excluded','as-served')
    liquid('19297',['game:jamhoneyportion'],1.3)
    liquid('02048',['game:vinegarportion'],1.01)
    liquid('14037',['game:spiritportion-*','game:alcoholportion','pemmican:alcohol-*'],.95,'distilled')
    liquid('14106',['game:ciderportion-*'],.99,'fermented')
    # Grain beers are not fruit wine. Exact grain codes take precedence.
    liquid('14003',['game:ciderportion-'+g for g in ['spelt','rye','amaranth','cassava']]+['pemmican:beer-*'],1.01,'fermented')
    liquid('43479',['game:ciderportion-rice'],.99,'fermented')
    salted_milk=alias('pemmican:saltmilkportion','game:milkportion',prep='salted')
    # Half a gram of table salt per 100 g serving; retain physical sodium.
    salted_milk['SodiumMgPer100G']+=196.5
    salted_milk['Per100G'].pop('electrolytes',None)
    for kind,ndb in [('green','11666'),('salt','11445'),('brown','11445'),('red','11446')]:
        record(ndb,['pemmican:algae-grind-'+kind],30,'Named wet seaweed composition; 30 g drained serving','ground')

    # Syrup and juice concentrations are explicit, independent of satiety. The
    # pack boils 1 L beet juice to .2 L syrup and 1 L cane juice to .4 L syrup.
    liquid('90480',['pemmican:syrup-cane'],1.35,'syrup')
    liquid('19335',['reference:sucrose'],1)
    # Refining excludes trace residues; sugar -> syrup is exactly one 100 g item per litre.
    for code in ['pemmican:sugar','reference:sucrose']:
        sugar=find(code);sugar['MicroPer100G']={k:0 for k in sugar['MicroPer100G']}
        sugar['Per100G']={'carbohydrate':100,'protein':0,'fat':0,'fiber':0}
        sugar['WaterGPer100G']=0
    dilute('pemmican:syrup-sugar','reference:sucrose',100/1040,1.04)
    dilute('pemmican:syrup-beet','reference:sucrose',.65,1.32)
    dilute('pemmican:sugarjuice-cane','pemmican:syrup-cane',.2*1.35/1.08,1.08)
    dilute('pemmican:sugarjuice-beet','pemmican:syrup-beet',.2*1.32/1.05,1.05)
    dilute('pemmican:sugarjuice-potato','pemmican:starch-dry',starch_mass/(125*1.05),1.05)
    for plant,parent in [('sugarcane','pemmican:sugarjuice-cane'),('sugarbeet','pemmican:vegetable-sugarbeet-raw'),('potato','pemmican:vegetable-potato-raw')]:
        p=alias('pemmican:sugargrind-'+plant,parent,prep='ground')
        p['PortionG']=136 if plant=='sugarbeet' else 140;p['PortionBasis']='Ground plant serving 140 g; covers .125 L press yield plus retained solids; named plant/juice analogue'
    for kind in ['beet','cane','sugar','potato','corn']:
        parent='pemmican:syrup-'+kind if kind in ['beet','cane','sugar'] else 'pemmican:starch-dry'
        dilute('pemmican:wort-'+kind,parent,.15,1.04)
    # Fantasy medicinal plants use named botanical analogues. No fortified aloe
    # drink or pharmaceutical healing value is interpreted as vitamin C content.
    record('11206',['pemmican:aloe'],50,'Raw cucumber peeled; watery edible gel analogue, declared 50 g portion')
    alias('pemmican:healbulb','game:vegetable-onion')
    dilute('pemmican:medjuice-aloe','pemmican:aloe',.8)
    dilute('pemmican:medjuice-healbulb','pemmican:healbulb',.8)
    dilute('pemmican:medrefined-healbulb','pemmican:healbulb',1)
    liquid('14106',['pemmican:medbrew-raw','pemmican:medbrew-fine'],.99,'fermented')

    # Blood is an explicit whole-blood model, not milk or fatty blood sausage.
    # 150 g/L hemoglobin * 3.47 mg Fe/g Hb gives 520.5 mg Fe/L. Protein
    # includes plasma proteins; remaining micros use declared trace assumptions.
    blood=copy.deepcopy(find('game:milkportion'))
    blood.update(Codes=['butchering:bloodportion'],Source='Seraph whole-blood composition model; hemoglobin iron stoichiometry',
        Coverage='substitution',Preparation='raw',PortionG=105,Density=1.05,WaterGPer100G=80,
        PortionBasis='1.05 g/mL; Hb 150 g/L, Fe 3.47 mg/g Hb; protein 19 g/100 g; explicit trace nutrient model',
        # Whole-blood trace electrolytes model: 320 mg sodium and 200 mg
        # potassium per 100 g; values remain physical composition inputs.
        SodiumMgPer100G=320,PotassiumMgPer100G=200,
        Per100G={'carbohydrate':.1,'protein':19,'fat':.2,'fiber':0},
        MicroPer100G={'calciumMg':6,'ironMg':49.57,'zincMg':.2,'vitaminAUgRAE':10,'vitaminCMg':1,
                     'b1Mg':.02,'b6Mg':.05,'folateUgDFE':10,'b12Ug':.5})
    # Keep Hb-derived mg intact, but calibrate its game reserve yield to 6%.
    # 0.2 L blood then supplies 11.45 points (about half a day's 22 points).
    # This is a gameplay coefficient, not a physiological absorption claim.
    blood['MicroReserveScale']={'ironMg':.06}
    foods.append(blood)
    record('13328',['butchering:offal-clean','butchering:offal-bloody'],50,
           'Beef tripe analogue for cleaned intestinal sausage casing; declared 50 g portion')
    for kind,parts in [
        ('bloodsausage',[(blood,2),(find('game:redmeat-raw'),2),(find('butchering:offal-clean'),1)]),
        ('blackpudding',[(blood,8),(find('game:fat-rendered'),1),(find('butchering:offal-clean'),1)])]:
        raw=f'butchering:sausage-{kind}-raw'
        mix(raw,parts,'Butchering blood sausage recipe; blood, meat/fat and edible casing; redmeat variant for shared sausage output','raw')
        cooked=f'butchering:sausage-{kind}-cooked'
        dehydrate(cooked,raw,data['07005']['255'],'07005','One raw sausage per cooked sausage; measured blood sausage moisture analogue')
        find(raw)['CookedReference']=cooked
        dehydrate(f'butchering:sausage-{kind}-smoked',cooked,35,'19002','One cooked sausage per smoked sausage; declared 35 percent final moisture model')
    for grain in ['spelt','rye','flax','rice','cassava','amaranth','sunflower']:
        dough=f'butchering:blooddough-{grain}'
        mix(dough,[(find('game:flour-'+grain),1),(blood,2)],'Butchering: one flour + .2 L blood','dough')
        perfect=f'butchering:bloodbread-{grain}-perfect'
        remove(perfect)
        dehydrate(perfect,dough,data['18077']['255'],'18077','One blood dough per loaf; blood iron and flour retained')
        for state in ['partbaked','charred']:alias(f'butchering:bloodbread-{grain}-{state}',perfect,prep=state)
    # Include static candidates which have no native direct-eating nutrition.
    for code,parent in [('pemmican:kebab-raw','pemmican:kebab-cooked'),('pemmican:kebab-mushroom-raw','pemmican:kebab-mushroom')]:
        alias(code,parent,prep='raw')
    for state,parent in [('raw','pemmican:pemmican-raw'),('dried','pemmican:pemmican-bar')]:
        for salt in ['basic','salted']:alias(f'game:pemmican-{state}-{salt}',parent,prep=state)

    remove('pemmican:longmilk-dry')
    milk_litre=alias('reference:milk-litre','game:milkportion',100)
    dehydrate('pemmican:longmilk-dry','reference:milk-litre',data['01212']['255'],'01212',
              'One litre milk per powder; input milk dry matter and minerals conserved')
    # Pressing yields 70 percent of edible mass, excluding fibre. The remaining
    # pulp is not counted as an additional human food in this pack.
    for p in list(foods):
        if not p['Codes'][0].startswith('game:juiceportion-'): continue
        fruit=p['Codes'][0].split('juiceportion-')[1]
        parent=find('game:fruit-'+('redapple' if fruit=='apple' else fruit))
        p['Density']=1;p['PortionG']=10
        p['Per100G']=dict(parent['Per100G']);p['Per100G']['fiber']=0
        p['MicroPer100G']=dict(parent['MicroPer100G'])
        p['WaterGPer100G']=parent['WaterGPer100G']+parent['Per100G']['fiber']
        p['Source']=parent['Source'];p['Coverage']='recipe'
        p['PortionBasis']='Press: 0.7 mL per gram input edible fruit, fibre left in pulp; remaining nutrients at parent concentration'

    # Remaining named families use explicit edible portions. Native satiety must
    # not inflate a lean fish chunk into a kilogram or alter a recipe in a meal.
    portions={
        'butchering:primemeat-*':250*77/113, 'game:fishchunk-*':180,
        'butchering:smoked-none-primemeat':50,'butchering:smoked-healing-primemeat':50,
        'butchering:sausage-bloodsausage-*':150, 'pemmican:chips-*':50,
        'pemmican:cookedvegetable-potato-*':213,'pemmican:cookedvegetable-corn-*':102,
        'pemmican:roastvegetable-potato-*':213,'pemmican:roastvegetable-corn-*':102,
        'primitivesurvival:caviar-*':85,
    }
    for p in foods:
        if p['PortionG']>0:continue
        code=p['Codes'][0]
        mass=portions.get(code)
        if mass is None and code.startswith('primitivesurvival:smokedmeat-'):
            kind=code.split('-')[1]
            mass=find('game:'+kind+('-smoked' if kind!='poultry' else '-cooked'))['PortionG']
        if mass is None:raise ValueError('Missing explicit portion: '+code)
        p['PortionG']=mass;p['PortionMethod']='explicit'
        p['PortionBasis']=f'Named food composition; declared edible serving {mass:g} g; invariant under native satiety and meal category'
    for shape in ['standard','long']:
        for size in [2,4,7,9]:
            alias(f'game:fishchunk-{shape}-{size}-cooked','game:fish-cooked',size/4,
                  basis=f'Cooked cod fillet 180 g at size 4; game fish chunk size {size}/4, same edible density')

    # B-complex must not disappear when any one of its four components is absent.
    # Every ingredient now has all fields, and composites retain all nine.
    for p in foods:
        macro=p['Per100G']
        p['EnergyKJPer100G']=macro.get('carbohydrate',0)*17+macro.get('protein',0)*17+macro.get('fat',0)*37
