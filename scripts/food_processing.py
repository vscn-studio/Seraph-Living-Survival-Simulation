"""Explicit Seraph cooking model, applied to nutrient amounts, never to concentrations alone.

These conservative coefficients are simulation parameters where a matched cooked
food record is unavailable, not laboratory measurements or USDA retention data.
Minerals remain in the food/broth; heat reduces vitamins. Drying separately removes
water and increases concentration without creating nutrients. Keep in sync with
FoodMicronutrients.CookingRetention, exercised by the release intake tests.
"""
RETENTION = {"calciumMg": 1, "ironMg": 1, "zincMg": 1, "vitaminAUgRAE": .8, "vitaminCMg": .5,
             "b1Mg": .7, "b6Mg": .8, "folateUgDFE": .7, "b12Ug": .8}
