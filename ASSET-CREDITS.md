# Asset Credits and Licensing

Project code and project-created assets are copyright HansJack under the MIT License in LICENSE, except where stated below. Project MIT does not relicense third-party assets. Mod authors: HansJack, ch4Ver, buzidao9, Zhiketangjiang2 and KitamuraEri.

## Status icons

- icons/status/ch4Ver/: artwork by ch4Ver, MIT licensed. Packaged copies are in assets/vsslssmod/textures/gui/status/ch4ver/. Full terms: THIRD-PARTY-NOTICES/Status-Icons-MIT.txt.
- icons/status/HansJack/: artwork by HansJack, including anemia, bone weakness, ketosis and dark adaptation, CC0. Packaged copies and derivatives are in assets/vsslssmod/textures/gui/status/hansjack/. Full terms: THIRD-PARTY-NOTICES/CC0-1.0.txt.
- Each packaged author directory contains COPYRIGHT.txt and attribution.json with per-file author, source, license, dimensions and hashes. Original source artwork remains in the corresponding author directory under icons/status in the repository.

## Models

- Bathtub and its part/inventory variants: copyright Zhiketangjiang2.
- Sediment diversion pot (drainpot and pipe/inventory variants) and water purification filter pot (filterpot and inventory variant, also used by watertreatment): copyright buzidao9.
- These creators authorized use in this project; all other rights remain with them. Permissions were confirmed by the project maintainer on 2026-11-08. This is project-specific permission, not MIT relicensing. See THIRD-PARTY-NOTICES/Authorized-Models.txt; contact the creators for other uses.
- Rainwater reservoir cover, well head, well wall, chimney exhaust and copper bathtub: created by HansJack, MIT. Referenced native game models and textures, including copper and water, remain the property of their owners.
- Stone-ring firepit: runtime shapes in `assets/vsslssmod/shapes/block/stonefirepit.json` and `assets/vsslssmod/shapes/block/stonefirepit-ring.json`. Copyright KitamuraEri (喜多村樱璃Eri). The author authorized use in this mod, confirmed by the project maintainer on 2026-11-21. This is project-specific permission; the model is not relicensed under MIT. See THIRD-PARTY-NOTICES/Authorized-Models.txt.
- The stone firepit block definition adapts native Vintage Story firepit settings; native log/ash geometry and textures remain Anego Studios material. World rendering references native firepit shapes and textures from the installed game.

## Player inspection images

assets/vsslssmod/textures/gui/body-player contains a generated front outline and six region masks used by the current HUD. The underlying Seraph model and skin belong to Anego Studios and are not relicensed as MIT. No player 3D shapes or original skin atlas are distributed. COPYRIGHT.txt and provenance.json in that directory retain source hashes and processing information.

## Heartbeat

assets/vsslssmod/sounds/body/heartbeat.ogg is a trimmed, faded mono excerpt of the supplied recording by Jurij (soundreality) from Pixabay. This audio is not covered by the project MIT license.

Author: https://pixabay.com/users/soundreality-31074404/
Original attribution: https://pixabay.com/sound-effects//?utm_source=link-attribution&utm_medium=referral&utm_campaign=music&utm_content=137135
License summary: https://pixabay.com/service/license-summary/
Terms: https://pixabay.com/service/terms/

## Food composition data

Food references credit USDA SR Legacy (April 2018), https://fdc.nal.usda.gov/. assets/vsslssmod/config/foods.json identifies reference foods, portions, substitutions, recipe calculations and fallbacks. The generator verifies the source archive SHA256; the downloaded archive is not redistributed. Processing retention, nutrient reserve conversions and yield calibrations are Seraph simulation parameters, not USDA measurements.

## Referenced implementation and dependencies

The sampled map-cell hover and border rendering pattern references ProspectTogether by p3t3rix and Wooza. Its copyright and full MIT terms are retained in THIRD-PARTY-NOTICES/ProspectTogether-MIT.txt.

Vintage Story, Cairo, Harmony, protobuf-net, Real Smoke and VSImGui assemblies are referenced from local installations and are not redistributed. Compatibility hooks do not imply ownership of other mods' code or resources.
