# Leviers de spawn supplémentaires — référence

Documentation des 4 leviers de spawn de Doriath qui **ne passent pas** par
`AIDirectorConfig` (voir [`AIDirectorConfig_Reference.md`](AIDirectorConfig_Reference.md) pour celui-là) :

| Levier | Où | Type |
|---|---|---|
| `BossSpawnPowerIndexBudgetAdjustedHardcoded` | `Doriath.Common/Hardcoded/` | Transpiler, pas de JSON |
| `BossSpawnBudgetAdjustedHardcoded` | `Doriath.Common/Hardcoded/` | Transpiler, pas de JSON |
| `Floor2SpawnBudgetReducedHardcoded` | `Doriath.Common/Hardcoded/` | Postfix, pas de JSON |
| `MonsterDeckOverridden` | ruleset JSON | règle native HouseRules |

Dépôt : **https://github.com/runiiii60/Demeo-DoriathProgressiveMod** — les trois
patches sous [`Doriath.Common/Hardcoded/`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/tree/master/Doriath.Common/Hardcoded),
leur appel dans [`Plugin.cs`](https://github.com/runiiii60/Demeo-DoriathProgressiveMod/blob/master/Doriathprogressive/Plugin.cs).

Les trois premiers sont partagés par les deux modes (Doriath.Common) et
appliqués depuis `Plugin.cs`, hors du système de règles HouseRules : ils ne
figurent donc **pas** dans le panneau des règles actives en jeu et ne sont pas
réglables sans recompiler. Le quatrième est du JSON pur.

---

## 1. La chaîne de calcul, et où chaque levier s'insère

```
                 ┌─ données de Dread du niveau (non moddable)
                 │
 budget du round │  GetAmbientPowerIndexDelta()      ← exploration normale
                 │  GetDifficultPowerIndexDelta()    ← pics de difficulté
                 └──────────────┬───────────────────────────────────
                                │  ×0.85 au floor 2 seulement
                                │  [Floor2SpawnBudgetReducedHardcoded]
                                ▼
        × EasySpawn/NormalSpawnBudgetMultiplier   [AIDirectorConfig]
                                ▼
                        CustomSpawn() dépense le budget
                                │
                                │  bornes de coût par monstre : 1 à 99
                                │  (natif 5 à 99, combat de boss uniquement)
                                │  [BossSpawnBudgetAdjustedHardcoded]
                                ▼
                 pioche dans le deck du floor  [MonsterDeckOverridden]
                                ▼
            plafonds : saturation de zone, compteurs par zone,
            hard cap plateau, soft roof puissance  [AIDirectorConfig]
```

Chemin séparé pour le début du combat de boss :

```
 SpawnBossAndMinions()
   └─ GetDifficultPowerIndexLevel()  ──×3──▶  PowerIndexPoints de CustomSpawn
                                     [BossSpawnPowerIndexBudgetAdjustedHardcoded]
```

**Les deux leviers « Boss » sont complémentaires, pas redondants** : l'un change
le budget total disponible, l'autre quels monstres sont éligibles à le dépenser.

---

## 2. `BossSpawnPowerIndexBudgetAdjustedHardcoded` — ×3 sur le budget du boss

| | |
|---|---|
| Paramètre | `Multiplier = 3f` |
| Natif | 1.0 (pas de multiplication du tout) |
| Cible | `AIDirectorController2.SpawnBossAndMinions` |
| Technique | Transpiler |

### Ce qu'il fait

`SpawnBossAndMinions()` place d'abord les pièces fixes du type de boss (le boss,
son escorte, les tuiles de trône — positions codées en dur par type de boss),
puis appelle **une seule fois** `CustomSpawn(...)` pour ajouter des monstres
supplémentaires autour. Le budget de cet appel vient de
`PowerIndexPoints = dataHelper.GetDifficultPowerIndexLevel(currentLevelIndex)`.

Ce patch multiplie la valeur retournée **à ce seul endroit** : ×3 de monstres
d'escorte supplémentaires (en volume ou en force, le budget achetant
indifféremment).

### Pourquoi un transpiler et pas un Postfix sur le helper

`GetDifficultPowerIndexLevel` est partagé par **quatre** systèmes :
`GetDifficultPowerIndexDelta()` (spawn ambiant normal), `HandleSpikeLock()`,
`SpawnKeyholder()` et ce spawn de boss. Un Postfix sur le helper aurait triplé
les trois autres au passage. Le transpiler multiplie le résultat sur la pile au
site d'appel, en laissant le helper intact.

L'insertion utilise exactement l'idiome du jeu lui-même (`conv.r4` ; `ldc.r4` ;
`mul` ; `Mathf.RoundToInt`), relevé dans `HandleSpikeLock()`.

### Ancrage et sécurité

L'ancre est `callIndex - 7` = l'appel `callvirt GetDifficultPowerIndexLevel`,
validé **par nom de méthode**, pas par une valeur littérale. C'est volontaire :
ça rend ce patch insensible au fait que `BossSpawnBudgetAdjustedHardcoded` ait
déjà réécrit les littéraux en `callIndex-5/-4` de la *même* méthode. Les deux
transpilers s'appliquent l'un après l'autre sans se marcher dessus.

Si le pattern n'est pas trouvé (mise à jour du jeu), le patch **ne fait rien** et
loggue un avertissement — valeur native conservée, aucun risque de corrompre le
corps de la méthode.

### Implications

- Le ×3 ne s'applique **qu'au moment du déclenchement du combat de boss**, pas au
  reste du floor du boss (celui-ci passe par les deltas ambiants normaux).
- Il est quand même soumis aux plafonds d'`AIDirectorConfig` : si
  `MaxNumberOfUnitsOnBoardHardCap`, la saturation de zone ou les compteurs par
  zone sont atteints, une partie du budget triplé est perdue **silencieusement**.
  Monter ce multiplicateur sans vérifier les plafonds ne garantit rien.
- C'est aussi le levier qui fait le plus consommer de cartes du `BossDeck` d'un
  seul coup (voir § 5).
- Historique des valeurs : 1.5 → 2.025 → 2.5 → 3.84 → **3**.

---

## 3. `BossSpawnBudgetAdjustedHardcoded` — élargit les monstres éligibles

| | |
|---|---|
| Paramètres | `MinCost = 1`, `MaxCost = 99` |
| Natif | `minPowerIndexCost = 5`, `maxPowerIndexCost = 99` |
| Cible | `AIDirectorController2.SpawnBossAndMinions` (même méthode que § 2) |
| Technique | Transpiler |

### Ce qu'il fait

Les deux bornes de coût passées à `CustomSpawn` sont des littéraux IL (natif 5
et 99) — aucun champ, aucune config à modifier, d'où le transpiler. Elles
filtrent quels monstres du deck sont éligibles au budget : un monstre dont le
coût en power index sort de l'intervalle est rejeté.

`MinCost` abaissé de 5 à 1 rend les monstres les moins chers à nouveau
éligibles. Conséquence directe : **plus d'ennemis pour un même budget**, plutôt
que quelques gros. `MaxCost` est laissé au natif (99 = pas de plafond effectif).

### Ancrage et sécurité

Le point d'insertion est localisé par l'appel à `CustomSpawn`, **pas** en
cherchant les littéraux 5 et 99 — ces valeurs sont trop communes et
matcheraient ailleurs dans la méthode. Puis `callIndex-5` doit être
`ldc.i4.5` et `callIndex-4` doit être `ldc.i4.s 99` : si l'un des deux ne
correspond pas (arguments réordonnés, code inséré entre eux par une mise à
jour), le patch est annulé entièrement plutôt que d'appliquer un demi-patch.

### Implications

- Effet **qualitatif**, pas quantitatif : ça ne donne pas plus de budget, ça
  change ce qu'on peut acheter avec. Combiné au ×3 du § 2, l'effet se cumule
  (plus de budget ET des unités moins chères = beaucoup plus d'unités).
- Effet de bord sur le deck : chaque rejet pour cause de coût hors bornes
  **consomme aussi une carte** (§ 5). Élargir l'intervalle réduit donc le nombre
  de rejets, ce qui est plutôt favorable à la réserve du deck — le seul des
  leviers de ce document qui soulage la pression sur le `BossDeck` au lieu de
  l'aggraver.
- La borne basse à 1 laisse passer les unités les plus faibles : si le `BossDeck`
  contient beaucoup de petites unités, le combat de boss peut devenir une masse
  de trash mobs plutôt qu'une escorte d'élite. C'est un choix de design, à
  arbitrer via la composition du `BossDeck`, pas via cette borne.
- Le spawn ambiant « normal » a ses propres littéraux (`DynamicSpawning`, natif
  **1 / 99**) — déjà équivalents à ce qu'on force ici, donc pas patchés.

---

## 4. `Floor2SpawnBudgetReducedHardcoded` — −15 % sur le floor 2 seulement

| | |
|---|---|
| Paramètres | `Multiplier = 0.85f`, `TargetFloorIndex = 2` |
| Cible | `AIDirectorDataHelper.GetAmbientPowerIndexDelta` et `GetDifficultPowerIndexDelta` |
| Technique | Postfix (×2 méthodes) |

### Pourquoi ce patch existe

Constat en jeu : beaucoup plus d'ennemis au floor 2 qu'au floor 3. Trois causes
natives identifiées, qui expliquent que le floor 3 en ait **moins** :

1. `GetPowerIndexForCrossover` applique **−3.0** de difficulté quand
   `IsForestAdventure() && GetCurrentPlayableFloorIndex() == 3` — l'aventure
   Doriath finissant sur RootLord (boss forêt), c'est appliqué.
2. Au floor du boss, `CustomSpawn` pioche dans le `BossDeck` pour **tous** les
   spawns (pas seulement le combat) : un deck plein de grosses unités = moins
   d'unités par budget.
3. `ActivePowerIndexInLevelSoftRoof` compte la puissance déjà sur le plateau : le
   RootLord et ses gardes en consomment une grande part.

**Blocage :** aucun champ d'`AIDirectorConfig` n'est par étage — la liste
complète des champs a été vérifiée par décompilation, ils sont tous globaux.
D'où ce patch, qui réduit le budget là où il est calculé.

### Ce qu'il fait

Postfix sur les deux « deltas » de budget (les deux `public`, retour `int`,
vérifié par signature) :

- `GetAmbientPowerIndexDelta()` — apparitions normales pendant l'exploration ;
- `GetDifficultPowerIndexDelta(context, levelIndex)` — pics de difficulté.

`__result` est multiplié par 0.85 **uniquement si** :

```csharp
if (__result <= 0) return;                 // garde-fou, voir ci-dessous
var floor = ...GetCurrentPlayableFloorIndex();
if (floor != TargetFloorIndex) return;     // floors 1 et 3 intacts
__result = (int)(__result * Multiplier);
```

### Le garde-fou `__result <= 0` — point important

Le delta est « puissance cible moins puissance ennemie déjà présente sur le
plateau ». **Un delta négatif signifie qu'il y a déjà trop d'ennemis.** Le
multiplier par 0.85 le rapprocherait de zéro, ce qui autoriserait **plus** de
spawns — exactement l'inverse de l'effet voulu. D'où le `return` sur les valeurs
négatives ou nulles. À ne pas retirer en croyant simplifier.

### Implications

- Le budget du combat de boss n'est **pas** touché : il passe par
  `GetDifficultPowerIndexLevel` (§ 2), pas par les deltas.
- La troncature `(int)` arrondit vers le bas, donc la réduction réelle est un
  peu supérieure à 15 % sur les petits deltas (un delta de 3 → 2, soit −33 %).
  Négligeable sur les gros deltas, mais c'est la raison pour laquelle le réglage
  se juge en jeu et pas sur le papier.
- Valeur retenue après un premier essai à 0.75 (−25 %), jugé trop fort.
- Pour viser un autre étage, changer `TargetFloorIndex`. Pour appliquer un
  coefficient différent par étage, il faudrait remplacer les deux constantes par
  une table — pas fait, pas nécessaire aujourd'hui.

---

## 5. `MonsterDeckOverridden` — la réserve de cartes

Règle **native** HouseRules (pas un patch Doriath), configurée en JSON. Elle
définit, par floor, quels types de monstres sont piochables et en quelle
quantité, plus les rôles spéciaux.

### Mécanique réelle du deck

Décompilation de `MonsterDeck.DrawEntry` et
`MonsterDeckOverriddenRule.CreateSubDeck`. Point clé : **les cartes sont
consommées, le deck n'est jamais remélangé ni rechargé.**

- `CreateSubDeck` : une valeur **N > 0** crée N copies avec
  `isRedrawEnabled = false` ; une valeur **0** crée **UNE** copie avec
  `isRedrawEnabled = true`.
- `DrawEntry` prend l'élément 0, fait `RemoveAt(0)`, et **ne le remet à la fin
  que si `isRedrawEnabled`**. Donc : une entrée à N s'épuise, une entrée à 0 est
  recyclée à l'infini.
- La surcharge `DrawEntry(deckType, maxPowerIndex, ...)` boucle jusqu'à 99 fois
  tant que le monstre pioché coûte plus cher que le budget restant — **chaque
  rejet consomme aussi une carte**. C'est la source de consommation la plus
  sous-estimée.
- `CustomSpawn` appelle `DrawEntry(..., dataHelper.IsBossLevel(), ...)` : sur le
  floor du boss, **tous** les spawns (ambiants, pics, combat de boss) piochent
  dans le `BossDeck`.
- Deck vide → `get_Item(0)` sur une liste vide → exception non gérée → la
  coroutine de chargement du niveau meurt silencieusement : **le niveau ne charge
  jamais, sans message d'erreur**. C'est l'explication du « bug du niveau qui ne
  charge pas ».

### Ce que la taille du deck ne fait PAS

La taille d'un deck **ne pilote pas** le nombre d'ennemis — c'est le budget qui
le fait. Le deck ne définit que :

1. les **proportions** entre types de monstres (une entrée à 16 est piochée deux
   fois plus souvent qu'une à 8) ;
2. la **réserve** disponible avant épuisement.

Monter un deck ne rend pas une carte plus difficile ; ça la rend seulement
survivable à un budget élevé.

### Config actuelle de Doriath (PROGRESSIVE)

| Deck | Types | Cartes consommables |
|---|---|---|
| `EntranceDeckFloor1` | 24 | 183 |
| `ExitDeckFloor1` | 25 | 199 |
| `EntranceDeckFloor2` | 21 | 127 |
| `ExitDeckFloor2` | 20 | 132 |
| `BossDeck` | 20 | 159 + 2 entrées infinies |

Rôles spéciaux : `Boss` = `RootLord`, `KeyHolderFloor1` = `ElvenSummoner`,
`KeyHolderFloor2` = `MotherCy`.

### Le filet anti-crash : deux entrées à 0

Dans le `BossDeck`, `ElvenSpearman: 0` et `GoblinFighter: 0` sont des cartes
recyclées à l'infini. Conséquence : **le `BossDeck` ne peut plus jamais se
vider**, donc le crash de chargement est structurellement impossible sur le
floor du boss.

Contrepartie assumée : une fois les 159 cartes consommables épuisées, seuls
ElvenSpearman et GoblinFighter continueraient d'apparaître. Un combat de boss
très long dégénérerait donc en vagues de ces deux unités plutôt qu'en plantage —
compromis volontaire.

**Les quatre decks des floors 1 et 2 n'ont pas ce filet.** C'est le point de
fragilité restant : y ajouter une entrée à 0 (un monstre faible et thématique)
coûterait une ligne de JSON et fermerait le dernier chemin vers le crash.

### Rôles `Boss` / `KeyHolderFloorN` — comportement hérité

Le rôle décide du comportement sur la carte, pas le monstre :

- `Boss` → `SpawnType.Boss` → placé dans la **salle de boss dédiée**, marquée sur
  la carte dès le début du floor (comportement natif du level design, aucun
  rapport avec le brouillard de guerre).
- `KeyHolderFloorN` → `SpawnType.Keyholder` → placé dans une zone de spawn
  ambiante ordinaire, donc **caché par le brouillard de guerre**, à trouver en
  explorant.

N'importe quel `BoardPieceId` assigné à `Boss` hériterait de la salle fixe
visible, et n'importe lequel assigné à `KeyHolderFloorN` hériterait du brouillard.

---

## 6. Interactions et pièges

**Deux transpilers sur la même méthode.** §2 et §3 patchent tous les deux
`SpawnBossAndMinions`. C'est volontaire et sûr, parce que §2 s'ancre sur un *nom
de méthode* et §3 sur l'appel à `CustomSpawn` — aucun des deux ne dépend d'un
littéral que l'autre modifie. En revanche, l'ordre d'application reste celui de
la liste dans `Plugin.cs` : y toucher demande de revérifier les deux ancrages.

**Les plafonds gagnent toujours.** Les trois leviers de budget peuvent être
annulés en pratique par `MaxNumberOfUnitsOnBoardHardCap`, les compteurs par zone
ou `AllowedSpawnZoneSaturation`. Aucun log ne signale un budget perdu pour cette
raison : si une hausse de budget ne change rien en jeu, regarder d'abord les
plafonds d'`AIDirectorConfig`.

**Le floor du boss cumule tout.** Sur ce floor : le `BossDeck` sert à tous les
spawns, le combat de boss reçoit ×3 de budget, les bornes de coût sont élargies,
et le −3.0 natif de l'aventure forêt réduit la difficulté de base. Quatre effets
qui se composent — c'est le floor le plus difficile à prévoir sur le papier et
celui qui demande le plus de tests.

**Aucun de ces trois patches ne se règle sans rebuild.** Choix assumé : ce sont
des réglages stabilisés, le JSON reste le point d'entrée pour ce qui doit bouger
souvent (`AIDirectorConfig`, `MonsterDeckOverridden`). Changer `Multiplier`,
`MinCost` ou `TargetFloorIndex` = modifier la constante et recompiler le DLL.

**Isolation des échecs.** Chaque patch est appelé dans son propre `try` dans
`Plugin.cs` (13 patches `*Hardcoded` au total) : un qui échoue — méthode
renommée par une mise à jour — n'empêche pas les autres. Le log indique
`Hardcoded patches applied (n/13)`.

---

## 7. Vérification dans le log BepInEx

Au démarrage du jeu (pas à l'activation du ruleset) :

```
[BossSpawnBudgetAdjustedHardcoded] Patch applied ... minPowerIndexCost=5 -> 1, maxPowerIndexCost=99 -> 99.
[BossSpawnPowerIndexBudgetAdjustedHardcoded] Patch applied ... multiplier x3 applied to the result of GetDifficultPowerIndexLevel().
[Floor2SpawnBudgetReducedHardcoded] Spawn budget for floor 2 multiplied by 0.85 (2 method(s) patched) — other floors unchanged.
[Plugin] Hardcoded patches applied (13/13).
```

Lignes à surveiller :

| Ligne | Signification |
|---|---|
| `Pattern IL attendu non trouve ...` | Mise à jour du jeu : le patch est annulé, valeur native conservée. Sans danger, mais le levier ne fait plus rien. |
| `... not found — patch skipped` | Type ou méthode renommé(e). Idem. |
| `(2 method(s) patched)` pour Floor2 | Doit afficher **2**. À 1, un seul des deux deltas est couvert → la réduction ne s'applique qu'à une moitié du spawn. |
| `applied (12/13)` ou moins | Un patch a lancé une exception — chercher `Hardcoded patch <nom> failed`. |

Pour le deck, pas de log dédié : le nombre réel de pioches par floor n'est pas
mesurable sans instrumentation (le budget est calculé à partir d'une table
d'équilibrage interne). Les tailles de deck restent un pari raisonnable, pas une
garantie.

---

## 8. Ce qui reste incertain

- La formule exacte de dépense du budget dans `CustomSpawn` : comment
  `minPowerIndexCost`/`maxPowerIndexCost` arbitrent entre « plus de monstres » et
  « des monstres plus forts ». L'effet constaté est « les deux », pas prouvé.
- Le nombre réel de pioches par floor, donc la marge de sécurité réelle des
  quatre decks sans entrée à 0.
- Si le −3.0 natif du floor 3 (aventure forêt) s'applique à toutes les
  configurations de boss ou seulement à certaines.

---

*Sources : `BossSpawnPowerIndexBudgetAdjustedHardcoded.cs`,
`BossSpawnBudgetAdjustedHardcoded.cs`, `Floor2SpawnBudgetReducedHardcoded.cs`,
`Plugin.cs`, `Doriath Progressive.json`, et la décompilation
d'`Assembly-CSharp.dll` (`SpawnBossAndMinions`, `CustomSpawn`,
`MonsterDeck.DrawEntry`, `MonsterDeckOverriddenRule.CreateSubDeck`,
`GetPowerIndexForCrossover`).*
