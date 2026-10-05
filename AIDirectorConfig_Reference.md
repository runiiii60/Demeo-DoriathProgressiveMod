# AIDirectorConfig — référence complète

Documentation de la règle HouseRules `AIDirectorConfig` (fichier
`AIDirectorConfigRule.cs`, mods Doriath (PROGRESSIVE) et Doriath (Point
Progressive) — nom de règle `DoriathPointAIDirectorConfig` côté Point).
Elle expose **19 constantes natives** de l'AI Director de Demeo, normalement
figées dans le code du jeu.

Valeurs natives extraites par décompilation d'`Assembly-CSharp.dll` (`.cctor`
de `Data.GameData.AIDirectorConfig` et `.ctor()` d'`AIDirectorController2`) —
ce sont bien les valeurs du jeu sans mod, pas des estimations.

---

## 1. Ce que fait l'AI Director

Demeo ne charge pas un nombre fixe d'ennemis au début d'une carte. Le système
(`Boardgame.AIDirector.AIDirectorController2`, même principe que Left 4 Dead)
découpe la carte en **zones** (`SpawnZone`) et dépense progressivement un
**budget de power index** par round, au fil de l'exploration et selon la
position des joueurs.

La boucle réelle, simplifiée :

1. Une zone est découverte → `HandleDiscoveredZone` calcule un budget à partir
   des données de « Dread » du niveau (`GetAmbientPowerIndexDelta` /
   `GetDifficultPowerIndexDelta`) multipliées par le pacing pattern courant.
2. Ce budget est multiplié par `EasySpawnBudgetMultiplier` ou
   `NormalSpawnBudgetMultiplier` selon la difficulté.
3. `CustomSpawn` dépense le budget en piochant des monstres dans le deck du
   floor (`MonsterDeckOverridden` du JSON), tant que :
   - la zone n'a pas atteint `AllowedSpawnZoneSaturation`,
   - les compteurs `MaxNumberOf{Easy,Medium,Strong}UnitsPerZone` ne sont pas
     saturés,
   - le plateau n'a pas atteint `MaxNumberOfUnitsOnBoardHardCap`,
   - la puissance active totale n'a pas dépassé
     `ActivePowerIndexInLevelSoftRoof`.

**Les 19 paramètres de cette règle n'agissent que sur les étapes 2 et 3.** Le
budget de base (étape 1) reste piloté par les données de Dread du niveau et
n'est pas exposé ici.

---

## 2. Deux familles de champs, deux mécanismes

| | Famille A | Famille B |
|---|---|---|
| Classe native | `Data.GameData.AIDirectorConfig` | `Boardgame.AIDirector.AIDirectorController2` |
| Nature | classe statique, 10 champs | champs d'instance, 9 champs |
| Technique | écriture réflexive directe | Postfix Harmony sur le `.ctor()` |
| Quand | `OnActivate()` (une fois par activation de ruleset, donc par carte/floor) | à chaque construction du contrôleur |

**Pourquoi `OnActivate()` et pas `Patch()` (bug corrigé le 2026-09-17) :**
`Patch(Harmony)` est appelé une seule fois, au tout début du chargement du
jeu, **avant** que le JSON du ruleset soit lu. Écrire les champs statiques
dans `Patch()` revenait à les écrire avec `_config` encore `null` — donc
toujours les valeurs natives, jamais celles du JSON. La famille B n'avait pas
ce problème : son Postfix relit `_config` **en direct** à chaque construction
de contrôleur, donc forcément après l'activation.

`RuntimeHelpers.RunClassConstructor` est appelé avant toute écriture, pour
forcer l'exécution du `.cctor` natif et garantir qu'il ne puisse pas
« rattraper » nos valeurs après coup.

**Conversion de type :** le JSON stocke tout en `double`. `TrySetNumericField`
convertit vers le vrai type du champ natif (`float`, `int` ou `double`), vérifié
par réflexion champ par champ — jamais supposé. Un `int` reçoit
`Math.Round(valeur)`.

**Robustesse :** aucun Transpiler IL ici, uniquement des écritures de champ.
Un champ renommé par une mise à jour du jeu produit un simple avertissement
dans le log et est ignoré — les 18 autres s'appliquent quand même, et rien ne
plante. C'est bien plus solide que `BossSpawnBudgetAdjustedHardcoded` (patch
IL dépendant d'un pattern exact).

---

## 3. Famille A — `Data.GameData.AIDirectorConfig` (10 champs statiques)

Colonne « Doriath » = valeur actuelle dans les deux rulesets Doriath
(PROGRESSIVE et Point Progressive ont des valeurs identiques).

### Budget

| Paramètre | Type | Natif | Doriath | Rôle |
|---|---|---|---|---|
| `EasySpawnBudgetMultiplier` | float | **0.3** | 1.26 | Multiplicateur du budget de spawn ambiant en difficulté Easy |
| `NormalSpawnBudgetMultiplier` | float | **1.0** | 2.88 | Idem, en difficulté Normal |
| `ActivePowerIndexInLevelSoftRoof` | int | **275** | 500 | Plafond **souple** de puissance totale active sur la carte |
| `MaxNumberOfUnitsOnBoardHardCap` | int | **50** | 150 | Plafond **dur** du nombre total d'unités sur le plateau |

**`EasySpawnBudgetMultiplier` / `NormalSpawnBudgetMultiplier`** — le levier le
plus direct sur la quantité d'ennemis. Multiplie le budget calculé à l'étape 1
avant toute dépense. Monter ces valeurs augmente le nombre **et** la force des
ennemis spawnés (le budget achète indifféremment des monstres chers ou
nombreux). Noter l'écart natif : Easy 0.3 contre Normal 1.0, soit un facteur
~3.3 entre les deux difficultés — si on veut garder ce ratio, les deux valeurs
doivent être montées ensemble (chez Doriath, 1.26 et 2.88 conservent un ratio
proche de 2.3).

**`ActivePowerIndexInLevelSoftRoof`** — plafond *souple* : le director cesse
d'ajouter de la pression quand la somme des power index des ennemis vivants
dépasse ce chiffre, mais ne despawn rien et ne bloque pas les spawns déjà
engagés. C'est le frein qui empêche une accumulation infinie quand le groupe
ne tue pas assez vite. Le laisser trop bas rend les multiplicateurs de budget
inopérants (on paie un budget qu'on ne peut pas dépenser) ; le monter très
haut rend le rythme de la carte dépendant uniquement de la vitesse de kill du
groupe.

**`MaxNumberOfUnitsOnBoardHardCap`** — plafond *dur*, toutes catégories
confondues. Aucune unité de plus n'apparaît, point. C'est le garde-fou de
performance (VR : chaque unité coûte en rendu et en temps de tour IA). Monter
ce chiffre a deux coûts concrets : des tours d'ennemis plus longs (le temps de
tour croît linéairement avec le nombre d'unités) et une pression accrue sur le
deck de monstres (voir § 6).

### Pourcentages

| Paramètre | Type | Natif | Doriath | Rôle |
|---|---|---|---|---|
| `AllowedSpawnZoneSaturation` | float | **0.8** | 0.85 | Fraction max des tuiles d'une zone occupées avant arrêt du spawn dynamique dans cette zone |
| `SpikeTrigger_MediumMap_ZoneDiscoveryPercentage` | float | **0.5** | 0.5 | % de carte moyenne à découvrir avant qu'un pic de difficulté puisse se déclencher |
| `SpikeTrigger_LargeMap_ZoneDiscoveryPercentage` | float | **0.6** | 0.6 | Idem, grande carte |

**`AllowedSpawnZoneSaturation`** — confirmé dans `CustomSpawn` par une
comparaison directe `spawnCount / totalTiles >= AllowedSpawnZoneSaturation`
→ arrêt du spawn dans la zone. Donc :
- **monter** (0.8 → 0.85) = zones plus densément peuplées, plus d'ennemis
  concentrés au même endroit ;
- **baisser** = zones plus clairsemées, mais le budget restant se reporte sur
  d'autres zones, donc une répartition plus étalée sur la carte plutôt que
  moins d'ennemis au total.

À 1.0 on autorise théoriquement des zones entièrement remplies — à éviter : un
couloir saturé peut rendre une zone infranchissable et bloquer la progression.

**`SpikeTrigger_*_ZoneDiscoveryPercentage`** — verrou temporel, pas quantitatif.
Un pic de difficulté ne peut pas se déclencher avant que ce % de carte soit
découvert (en plus d'une condition de rounds : 5 rounds minimum 3 sur carte
moyenne, 6 minimum 4 sur grande — ces valeurs ne sont pas exposées). Baisser
ce pourcentage fait arriver les pics plus tôt (groupe pris de court au début
du floor) ; le monter les repousse vers la fin, quand le groupe est plus
équipé. Noter qu'il n'existe **pas** d'équivalent pour les petites cartes :
le comportement y est déterminé ailleurs et n'est pas moddable par cette règle.

### Compteurs par zone

| Paramètre | Type | Natif | Doriath | Rôle |
|---|---|---|---|---|
| `MaxNumberOfEasyUnitsPerZone` | int | **7** | 16 | Plafond d'ennemis Easy par zone (`SpawnZone.numEasyEnemies`) |
| `MaxNumberOfMediumUnitsPerZone` | int | **5** | 10 | Idem, Medium (`numMediumEnemies`) |
| `MaxNumberOfStrongUnitsPerZone` | int | **4** | 7 | Idem, Strong (`numStrongEnemies`) |

Ces trois plafonds s'appliquent **par zone**, en plus du plafond global. La
classification Easy / Medium / Strong vient du power index du monstre, selon
deux seuils natifs non exposés : `EasyEnemiesMaxPowerIndex` = 7 et
`MediumEnemiesMaxPowerIndex` = 15 (au-delà = Strong). Un monstre dont on
modifie le power index via un autre levier change donc de catégorie et de
plafond.

Natif, le total par zone plafonne à 7 + 5 + 4 = **16 unités** ; chez Doriath,
16 + 10 + 7 = **33**. C'est ce qui détermine la densité maximale réelle d'une
rencontre, et c'est souvent ce qui *bride silencieusement* une hausse des
multiplicateurs de budget : si les compteurs sont saturés, le budget
excédentaire ne peut plus être dépensé dans cette zone. Les monter sans monter
le budget ne fait rien non plus — les deux vont par paire.

---

## 4. Famille B — `AIDirectorController2` (9 champs d'instance)

Ces champs servent à `TagSpawnZones()` pour classer les tuiles découvertes en
Zone1 / Zone2 / Zone2_OuterRing — c'est-à-dire **où** la carte est considérée
comme proche de l'entrée, intermédiaire ou périphérique, ce qui conditionne
quel type de spawn s'y applique.

| Paramètre | Type | Natif | Doriath |
|---|---|---|---|
| `Zone1_Large_PercentCoverage` | float | **0.25** | 0.25 |
| `Zone1_Medium_PercentCoverage` | float | **0.32** | 0.32 |
| `Zone1_Small_PercentCoverage` | float | **0.32** | 0.32 |
| `Zone2_Large_PercentCoverage` | float | **0.46** | 0.46 |
| `Zone2_Medium_PercentCoverage` | float | **0.44** | 0.44 |
| `Zone2_Small_PercentCoverage` | float | **0.42** | 0.42 |
| `Zone2_OuterRing_Large_PercentCoverage` | float | **0.0** | 0.0 |
| `Zone2_OuterRing_Medium_PercentCoverage` | float | **0.0** | 0.0 |
| `Zone2_OuterRing_Small_PercentCoverage` | float | **0.0** | 0.0 |

Les trois `Zone2_OuterRing_*` sont à **0.0 natif confirmé** : le code natif ne
les initialise jamais explicitement (vérifié par décompilation du `.ctor()` —
c'est un vrai zéro, pas une valeur manquante dans la recherche). Autrement dit
le tag `Zone2_OuterRing` existe dans l'enum `SpawnZoneTag` mais n'est pas
alimenté par ce mécanisme dans la version actuelle du jeu. Leur donner une
valeur non nulle active un chemin de code natif **jamais exercé par le jeu de
base** — à considérer comme non testé.

**Implications pratiques.** Ces 9 valeurs changent la *géographie* du spawn,
pas sa quantité : Zone1 ≈ 25-32 % de la carte (proche entrée), Zone2 ≈ 42-46 %.
Les monter concentre le spawn dans une portion plus large de carte « active » ;
les baisser laisse plus de tuiles en `Normal`. Comme Zone1 + Zone2 fait déjà
~71-76 % natif, pousser la somme au-delà de 1.0 est incohérent et le
comportement résultant n'est pas documenté côté natif. **Doriath les laisse
toutes aux valeurs natives** — ce sont les paramètres les moins compris des 19
et ceux qui offrent le moins de retour visible en jeu pour le risque pris.

---

## 5. Valeurs natives relevées mais NON exposées

Repérées dans le même `.cctor` (~53 constantes au total), conservées ici pour
référence au cas où il faudrait étendre la règle :

- `BalanceMultiplier` 1 / 2 / 3 joueurs = **0.33 / 0.55 / 0.77** — l'échelle
  selon la taille du groupe. Explique directement pourquoi une carte réglée
  pour 4 joueurs est brutale à 2 : le budget n'est pas divisé par deux, il est
  multiplié par 0.55 au lieu de 1.0.
- `WeakUnitsPowerIndex` = 5 ; `EasyEnemiesMaxPowerIndex` = 7 ;
  `MediumEnemiesMaxPowerIndex` = 15 — les seuils de classification cités au § 3.
- `KeyHolderMinDistanceToExit` = 10, `KeyHolderMaxDistanceToExit` = 30,
  `KeyHolderMinDistanceToEntrance` = 10 — placement du porteur de clé.
- `SpikeTrigger` rounds Medium / Large = 5 / 6 (minimum 3 / 4).
- `ZoneExclusionDistanceFromEntrance` = 10 — rayon autour de l'entrée où le
  spawn est interdit.
- `elvenSummonerMaxSpawnDistanceFromExit` — littéral, spécifique à
  l'ElvenSummoner.
- `SpikeZoneMaxBudget` (`SpawnZone`) — **champ mort côté gameplay** : toutes
  ses occurrences dans `AIDirectorController2` sont des copies de
  sérialisation (`SerializeTo` / `DeserializeFrom` / `GetSerializable`), jamais
  une décision de spawn. Ne pas perdre de temps à l'exposer.
- `minPowerIndexCost` / `maxPowerIndexCost` du spawn ambiant normal
  (`DynamicSpawning`, natif 1 / 99) — littéraux IL, exposables seulement par
  Transpiler.

---

## 6. ⚠ Implication critique : épuisement du deck de monstres

C'est le seul risque réel de cette règle, et il ne vient pas de la règle
elle-même.

Chaque unité spawnée **consomme une carte** du deck du floor
(`MonsterDeckOverridden` : `EntranceDeckFloor1/2`, `ExitDeckFloor1/2`,
`BossDeck`). Si le deck est trop petit pour le volume de spawn autorisé par ces
paramètres, le jeu pioche dans une liste vide → **exception non gérée qui tue
silencieusement la coroutine de chargement du niveau** : la carte ne charge
jamais, sans message d'erreur visible.

Le nombre de piochés dépendant de la carte et du seed, **le crash est
intermittent, pas systématique** — un test réussi ne prouve pas que la config
est sûre.

Règle de conduite : en montant `EasySpawnBudgetMultiplier`,
`NormalSpawnBudgetMultiplier`, `AllowedSpawnZoneSaturation` ou
`MaxNumberOfUnitsOnBoardHardCap` au-delà des valeurs actuelles, monter
proportionnellement les quantités de **tous** les decks de `MonsterDeckOverridden`.

Astuce connexe : une entrée de deck à **0** est traitée comme une carte
recyclée à l'infini (cf. `ElvenSpearman` et `GoblinFighter` à 0 dans le
`BossDeck` de Doriath) — c'est le filet de sécurité le plus simple contre
l'épuisement, à condition d'accepter le type de monstre concerné en quantité
illimitée.

---

## 7. Interactions avec les autres leviers de spawn du mod

`AIDirectorConfig` ne gouverne que le spawn ambiant générique. Les autres
leviers, cumulatifs :

| Levier | Où | Effet |
|---|---|---|
| `BossSpawnPowerIndexBudgetAdjustedHardcoded` | Doriath.Common, `Multiplier = 3f` (natif 1.0) | ×3 sur le budget de power index du spawn autour du boss |
| `BossSpawnBudgetAdjustedHardcoded` | Doriath.Common, `MinCost = 1`, `MaxCost = 99` (natif 5 / 99) | Abaisse le coût minimum → des monstres moins chers redeviennent éligibles, donc plus d'ennemis pour un même budget |
| `Floor2SpawnBudgetReducedHardcoded` | Doriath.Common, `Multiplier = 0.85f` | −15 % de budget sur le floor 2 uniquement |
| `MonsterDeckOverridden` | JSON, règle HouseRules native | Pondération des types piochés (jamais leur nombre) + réserve de cartes |

Point important : le spawn autour du boss est tagué `SpawnType.Ambient`, pas
`SpawnType.Boss` — le combat de boss réutilise le système ambiant. Donc les
paramètres d'`AIDirectorConfig` (plafonds par zone, hard cap, saturation)
s'appliquent **aussi** au combat de boss et peuvent brider les multiplicateurs
de boss ci-dessus sans que rien ne le signale.

Les trois règles `*Hardcoded` ne sont plus configurables par JSON (décision
assumée : bugfix/tuning stables, recompilation nécessaire pour les changer).
Seul `AIDirectorConfig` reste réglable à chaud dans le JSON, sans rebuild.

---

## 8. Entrée JSON et vérification

### a) Valeurs natives — comportement vanilla

Ce bloc reproduit exactement le jeu non moddé. Utile comme point de départ pour
un nouveau ruleset, ou pour revenir en arrière et comparer un test en jeu.

```json
{
  "Rule": "AIDirectorConfig",
  "Config": {
    "EasySpawnBudgetMultiplier": 0.3,
    "NormalSpawnBudgetMultiplier": 1.0,
    "ActivePowerIndexInLevelSoftRoof": 275,
    "MaxNumberOfUnitsOnBoardHardCap": 50,
    "AllowedSpawnZoneSaturation": 0.8,
    "SpikeTrigger_MediumMap_ZoneDiscoveryPercentage": 0.5,
    "SpikeTrigger_LargeMap_ZoneDiscoveryPercentage": 0.6,
    "MaxNumberOfEasyUnitsPerZone": 7,
    "MaxNumberOfMediumUnitsPerZone": 5,
    "MaxNumberOfStrongUnitsPerZone": 4,
    "Zone1_Large_PercentCoverage": 0.25,
    "Zone1_Medium_PercentCoverage": 0.32,
    "Zone1_Small_PercentCoverage": 0.32,
    "Zone2_Large_PercentCoverage": 0.46,
    "Zone2_Medium_PercentCoverage": 0.44,
    "Zone2_Small_PercentCoverage": 0.42,
    "Zone2_OuterRing_Large_PercentCoverage": 0.0,
    "Zone2_OuterRing_Medium_PercentCoverage": 0.0,
    "Zone2_OuterRing_Small_PercentCoverage": 0.0
  }
}
```

Équivalent plus court : omettre entièrement l'entrée `AIDirectorConfig` du
ruleset. Les `NativeDefaults` de la règle *sont* ces valeurs.

### b) Valeurs Doriath — la config réellement en place

Identique dans Doriath (PROGRESSIVE) et Doriath (Point Progressive). Seuls 7
champs sur 19 sont poussés : les 4 de budget et les 3 compteurs par zone, plus
la saturation. Les deux `SpikeTrigger_*` et les 9 `PercentCoverage` sont laissés
aux valeurs natives.

```json
{
  "Rule": "AIDirectorConfig",
  "Config": {
    "EasySpawnBudgetMultiplier": 1.26,
    "NormalSpawnBudgetMultiplier": 2.88,
    "ActivePowerIndexInLevelSoftRoof": 500,
    "MaxNumberOfUnitsOnBoardHardCap": 150,
    "AllowedSpawnZoneSaturation": 0.85,
    "SpikeTrigger_MediumMap_ZoneDiscoveryPercentage": 0.5,
    "SpikeTrigger_LargeMap_ZoneDiscoveryPercentage": 0.6,
    "MaxNumberOfEasyUnitsPerZone": 16,
    "MaxNumberOfMediumUnitsPerZone": 10,
    "MaxNumberOfStrongUnitsPerZone": 7,
    "Zone1_Large_PercentCoverage": 0.25,
    "Zone1_Medium_PercentCoverage": 0.32,
    "Zone1_Small_PercentCoverage": 0.32,
    "Zone2_Large_PercentCoverage": 0.46,
    "Zone2_Medium_PercentCoverage": 0.44,
    "Zone2_Small_PercentCoverage": 0.42,
    "Zone2_OuterRing_Large_PercentCoverage": 0.0,
    "Zone2_OuterRing_Medium_PercentCoverage": 0.0,
    "Zone2_OuterRing_Small_PercentCoverage": 0.0
  }
}
```

(Côté Point Progressive, le nom de règle est `DoriathPointAIDirectorConfig`,
avec les mêmes clés et, actuellement, les mêmes valeurs.)

Récapitulatif des écarts du bloc b) par rapport au natif :

| Champ | Natif | Doriath | Écart |
|---|---|---|---|
| `EasySpawnBudgetMultiplier` | 0.3 | 1.26 | ×4.2 |
| `NormalSpawnBudgetMultiplier` | 1.0 | 2.88 | ×2.9 |
| `ActivePowerIndexInLevelSoftRoof` | 275 | 500 | ×1.8 |
| `MaxNumberOfUnitsOnBoardHardCap` | 50 | 150 | ×3 |
| `AllowedSpawnZoneSaturation` | 0.8 | 0.85 | +0.05 |
| `MaxNumberOfEasyUnitsPerZone` | 7 | 16 | ×2.3 |
| `MaxNumberOfMediumUnitsPerZone` | 5 | 10 | ×2 |
| `MaxNumberOfStrongUnitsPerZone` | 4 | 7 | ×1.75 |
| 11 autres champs | — | — | natif inchangé |

Total par zone : 16 unités natif → 33 chez Doriath.

**Toutes les clés sont optionnelles.** Une clé omise retombe sur la valeur
native, donc une config vide = comportement vanilla. Une clé inconnue est
ignorée avec un avertissement dans le log.

**Vérification au chargement** — dans le log BepInEx, chercher :

```
[AIDirectorConfigRule] (OnActivate) AIDirectorConfig.<champ> = <valeur>
[AIDirectorConfigRule] AIDirectorController2.<champ> = <valeur>
```

19 lignes attendues (10 au premier type, 9 au second). Une ligne
`... non trouvé ...` signale un champ renommé par une mise à jour du jeu : sans
danger, mais ce champ garde sa valeur native et n'est plus moddable tant que le
nom n'est pas corrigé dans la règle.

Attention au moment de la lecture : les lignes `(OnActivate)` n'apparaissent
qu'à l'**activation du ruleset** (une fois par carte/floor), pas au démarrage
du jeu. Ne pas les voir juste après le lancement est normal.

---

## 9. Utiliser cette règle dans un autre ruleset (dépendance à `DoriathMod.dll`)

`AIDirectorConfig` n'est **pas** une règle native de HouseRules. C'est une règle
custom apportée par `DoriathMod.dll` : sans ce DLL, le nom `"AIDirectorConfig"`
n'existe nulle part et le ruleset qui l'utilise ne peut pas être importé.

### Dépôt et fichiers

Code source et releases : **https://github.com/runiiii60/Demeo-DoriathProgressiveMod**
(branche `master`, monorepo des deux modes)

| Fichier | Lien |
|---|---|
| La règle elle-même | [`Doriathprogressive/AIDirectorConfigRule.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/AIDirectorConfigRule.cs) |
| Version brute (copier/coller) | [raw](https://raw.githubusercontent.com/runiiii60/Demeo-DoriathProgressiveMod/master/Doriathprogressive/AIDirectorConfigRule.cs) |
| Enregistrement de la règle | [`Doriathprogressive/Plugin.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/Plugin.cs) |
| Ruleset d'exemple complet | [`Doriathprogressive/Doriath Progressive.json`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/Doriath%20Progressive.json) |
| Notes de décompilation | [`Doriathprogressive/AIDirector_Notes.md`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/AIDirector_Notes.md) |
| DLL compilé (`DoriathMod.rar`) | [Releases](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/releases) |

Variante Point Progressive : même arborescence sous `Doriathpointprogressive/`.

### Identité du DLL

| | |
|---|---|
| Nom de l'assembly | `DoriathMod.dll` (projet `ProgressiveMod.csproj`) |
| GUID BepInEx | `com.monnom.demeomods.progressive` |
| Version | 1.0.5 |
| Dépendance dure | `com.orendain.demeomods.houserules.core` |
| Dépendance souple | `com.orendain.demeomods.houserules.configuration` |

Le DLL enregistre 6 règles au démarrage, dont `AIDirectorConfigRule`. Le nom
JSON d'une règle est le nom de classe **sans le suffixe `Rule`** — d'où
`"AIDirectorConfig"`.

### Cas 1 — « je veux juste utiliser la règle dans MON ruleset JSON »

C'est exactement ce que fait `MagicGarden.json` : un ruleset purement JSON, qui
pioche cette seule règle custom dans `DoriathMod.dll`.

1. Installer `DoriathMod.dll` dans `BepInEx/plugins/` (à côté des DLL de
   HouseRules). Aucune configuration, aucun autre fichier.
2. Ajouter l'entrée dans le tableau `Rules` du ruleset (voir § 8 pour les clés).
3. Relancer le jeu, activer le ruleset, vérifier les 19 lignes de log.

**Il n'y a aucun champ de dépendance à déclarer dans le JSON.** Le format de
ruleset HouseRules ne connaît que `Name`, `Description`, `Enabled` et `Rules` —
la dépendance est entièrement implicite, résolue au moment de l'import par
présence ou absence du nom de règle dans le Rulebook.

**Conséquences à connaître :**

- **DLL absent = ruleset cassé, pas dégradé.** Le nom de règle inconnu fait
  échouer l'import du ruleset entier — pas seulement de cette règle. Un
  utilisateur qui télécharge un ruleset JSON l'utilisant sans avoir le DLL ne
  voit pas « 33 règles sur 34 », il voit un ruleset qui ne se charge pas. Un
  ruleset distribué publiquement doit donc indiquer la dépendance dans sa
  description ou son post de publication.
- **Les autres mods restent indépendants.** `DoriathMod.dll` apporte aussi 5
  autres règles (progression, XP, etc.) et des patches `*Hardcoded` toujours
  actifs dès que le DLL est chargé. Installer le DLL uniquement pour
  `AIDirectorConfig` embarque donc aussi ces patches — c'est actuellement un
  tout, pas un menu.
- **Multijoueur :** la règle est marquée `IMultiplayerSafe`, donc seul l'hôte a
  besoin du DLL ; le spawn étant décidé côté hôte, les invités n'ont rien à
  installer.
- **Pas de rebuild pour régler les valeurs.** Les 19 valeurs sont lues dans le
  JSON à chaque activation du ruleset : on peut les modifier et relancer le jeu
  sans recompiler quoi que ce soit. Seuls les patches `*Hardcoded` du § 7
  demandent une recompilation.
- **Attention au nom côté Point Progressive.** Ce mode enregistre la règle sous
  `DoriathPointAIDirectorConfig`. Les deux noms ne sont pas interchangeables :
  utiliser le nom Point sans `DoriathPointMod.dll` échoue de la même façon.

### Cas 2 — « je veux la même capacité dans MON propre mod C# »

Deux chemins, selon qu'on veut dépendre de `DoriathMod.dll` ou être autonome.

**a) Dépendre de `DoriathMod.dll`** — rien à écrire : il suffit de l'exiger comme
dépendance BepInEx de son propre plugin et d'utiliser `"AIDirectorConfig"` dans
son ruleset.

```csharp
[BepInDependency("com.monnom.demeomods.progressive")]
```

**b) Réimplémenter la règle dans son propre plugin** — le plus propre pour un mod
qui ne veut pas embarquer tout Doriath. Le fichier à reprendre :
[`AIDirectorConfigRule.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/AIDirectorConfigRule.cs).
Il est autonome :
il ne dépend que de HarmonyLib, `HouseRules.Core` et `Plugin.Log`. Pour le
reprendre :

1. Copier le fichier dans son projet, changer le `namespace` et remplacer
   `Plugin.Log` par son propre logger.
2. **Renommer la classe.** Deux mods qui enregistrent le même nom de règle
   entrent en conflit dans le Rulebook : si `DoriathMod.dll` est aussi installé,
   une seconde classe `AIDirectorConfigRule` produit un doublon de nom. C'est la
   raison d'être du préfixe `DoriathPoint*` côté Point Progressive — appliquer la
   même logique (`MonModAIDirectorConfigRule` → clé JSON
   `"MonModAIDirectorConfig"`).
3. Enregistrer la règle dans son `Awake()` :
   `HR.Rulebook.Register(typeof(MonModAIDirectorConfigRule));`
4. Garder les trois points de conception qui font que la règle fonctionne, et qui
   sont la vraie valeur du fichier :
   - l'écriture des champs **statiques** dans `OnActivate()`, jamais dans
     `Patch()` (sinon `_config` est encore `null` → valeurs natives, cf. § 2) ;
   - `RuntimeHelpers.RunClassConstructor` avant d'écraser quoi que ce soit ;
   - `TrySetNumericField`, qui convertit le `double` du JSON vers le vrai type du
     champ natif (`int` pour `MaxNumberOfUnitsOnBoardHardCap`, `float` pour
     `AllowedSpawnZoneSaturation`…) — vérifié par réflexion, jamais supposé.
5. Ne pas oublier les `NativeDefaults` : ce sont eux qui garantissent qu'une clé
   omise = comportement vanilla.

Dans les deux cas, l'avertissement du § 6 sur l'épuisement du deck de monstres
s'applique intégralement : un ruleset tiers qui monte les budgets sans monter
ses decks `MonsterDeckOverridden` produira des échecs de chargement de niveau
intermittents.

---

## 10. Ce qui reste incertain

À distinguer du reste de ce document, qui est confirmé par décompilation :

- La formule exacte de dépense du budget dans `CustomSpawn` (comment
  `minPowerIndexCost` / `maxPowerIndexCost` arbitrent entre « plus de monstres »
  et « des monstres plus forts ») n'a pas été tracée en détail.
- L'ordre de priorité entre `ActivePowerIndexInLevelSoftRoof` (souple) et les
  compteurs par zone quand les deux sont proches de la saturation.
- Ce que compte exactement `MaxNumberOfUnitsOnBoardHardCap` : « toutes unités
  confondues » est le libellé natif, mais la prise en compte des invocations,
  des props hostiles et des pièces joueur n'a pas été vérifiée.
- Le comportement des `Zone2_OuterRing_*` à une valeur non nulle (§ 4).

---

*Sources : décompilation d'`Assembly-CSharp.dll` (outil maison
`System.Reflection.Metadata`, sessions des 2026-09-16 et 2026-09-29),
`AIDirectorConfigRule.cs`, `AIDirector_Notes.md`, et les rulesets
`Doriath Progressive.json` / `Doriath PointProgressive.json`.*
