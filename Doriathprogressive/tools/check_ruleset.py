#!/usr/bin/env python3
"""
Consistency check for a Doriath HouseRules ruleset.

Usage:
    python tools/check_ruleset.py "Doriath (PROGRESSIVE).json"
    python tools/check_ruleset.py            # every .json in the script's parent folder

Output: a list of ERRORS (blocking) and WARNINGS (worth a look).
Exit code 1 when there is at least one error, 0 otherwise, so it can be used
as a build step.

The thresholds sit at the top of the file; adjust them if the balance changes.
"""
import json
import re
import sys
import glob
import os

# ── Thresholds ───────────────────────────────────────────────────────
MIN_CARDS_MAIN_DECK = 20      # Entrance/Exit: below this the deck loops
MIN_CARDS_BOSS_DECK = 4       # BossDeck: deliberately small
MIN_MAP_POOL = 6              # fewer than this gives repetitive sequences
FLOOR_PATTERN = re.compile(r'^(Forest|Elven|Desert|Towns)Floor\d{2}$')

HEROES = {"HeroBard", "HeroGuardian", "HeroHunter", "HeroRogue",
          "HeroSorcerer", "HeroWarlock", "HeroBarbarian"}

# Pieces that exist in game without ever appearing in a monster deck:
# summons, traps and props placed by the heroes.
NON_DECK_PIECES = {
    "WarlockMinion", "SellswordArbalestierActive", "Verochka", "Tornado",
    "GrapplingTotem", "SwordOfAvalon", "SmiteWard", "HealingBeacon",
    "IceElemental", "FireElemental", "ProximityMine", "SporeFungus",
    "GoldPile", "Barrel", "Lamp",
}

# Rules whose config is a dictionary keyed by piece name.
PIECE_KEYED_RULES = [
    "PieceImmunityListAdjusted",
    "PieceBehavioursListOverridden",
    "PieceAbilityListOverridden",
    "PieceUseWhenKilledOverridden",
    "PieceExtraStatsAdjusted",
]


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def rule(doc, name, default=None):
    for r in doc.get("Rules", []):
        if r.get("Rule") == name:
            return r.get("Config")
    return default


def check(path):
    errors, warnings = [], []

    try:
        doc = load(path)
    except json.JSONDecodeError as e:
        return ["unreadable JSON: %s" % e], []

    rules = doc.get("Rules", [])
    if not rules:
        errors.append("no rule in the ruleset")

    # ── Duplicate rules ──────────────────────────────────────────────
    seen = {}
    for r in rules:
        n = r.get("Rule")
        if not n:
            errors.append("a rule with no name")
            continue
        seen[n] = seen.get(n, 0) + 1
    for n, c in seen.items():
        if c > 1:
            errors.append("rule '%s' declared %d times — HouseRules will keep only one" % (n, c))

    # ── Map pool ─────────────────────────────────────────────────────
    pool = rule(doc, "MyRandomLevelSequenceOverridden")
    if pool is None:
        warnings.append("no MyRandomLevelSequenceOverridden — native level sequence")
    else:
        if len(pool) < MIN_MAP_POOL:
            errors.append("map pool has %d floors (minimum %d)" % (len(pool), MIN_MAP_POOL))
        dupes = {m for m in pool if pool.count(m) > 1}
        if dupes:
            errors.append("duplicate floors in the pool: %s" % ", ".join(sorted(dupes)))
        for m in pool:
            if not FLOOR_PATTERN.match(m):
                errors.append("suspicious floor name: '%s'" % m)
        biomes = {}
        for m in pool:
            b = FLOOR_PATTERN.match(m).group(1) if FLOOR_PATTERN.match(m) else "?"
            biomes[b] = biomes.get(b, 0) + 1
        for b, n in sorted(biomes.items()):
            if n < 2:
                warnings.append("only one floor of the %s biome in the pool" % b)

    # ── Monster decks ────────────────────────────────────────────────
    decks = rule(doc, "MonsterDeckOverridden", {}) or {}
    deck_pieces = set()
    for name, cards in decks.items():
        # KeyHolderFloorN / Boss entries name a single piece, not a deck.
        if isinstance(cards, str):
            deck_pieces.add(cards)
            continue
        if not isinstance(cards, dict):
            errors.append("deck '%s': unexpected format (%s)" % (name, type(cards).__name__))
            continue
        deck_pieces |= set(cards)
        total = sum(cards.values())
        if total == 0:
            errors.append("deck '%s' is empty" % name)
            continue
        floor = MIN_CARDS_BOSS_DECK if ("Boss" in name or "KeyHolder" in name) else MIN_CARDS_MAIN_DECK
        if total < floor:
            errors.append("deck '%s': %d cards (minimum %d)" % (name, total, floor))
        for piece, n in cards.items():
            if n < 0:
                errors.append("deck '%s': '%s' has a negative count (%d)" % (name, piece, n))

    # ── Dead config: a piece configured but never spawned ────────────
    # A piece can reach the board from a deck, as a hero, as a summon or prop,
    # or through an ability that spawns it (AbilityRandomPieceList:
    # RatKingRatBomb, SpawnGreaterMonster, and so on).
    spawnable = deck_pieces | HEROES | NON_DECK_PIECES
    for spawned in (rule(doc, "AbilityRandomPieceList", {}) or {}).values():
        if isinstance(spawned, list):
            spawnable |= set(spawned)

    dead = {}
    for rname in PIECE_KEYED_RULES:
        cfg = rule(doc, rname)
        if not isinstance(cfg, dict):
            continue
        for piece in cfg:
            if piece not in spawnable:
                dead.setdefault(piece, set()).add(rname)

    for entry in rule(doc, "PieceConfigAdjusted", []) or []:
        piece = entry.get("Piece")
        if piece and piece not in spawnable:
            dead.setdefault(piece, set()).add("PieceConfigAdjusted")

    for piece in sorted(dead):
        warnings.append("'%s' is configured (%s) but appears in no deck and no spawn — "
                        "dead config, or a piece to put back in a deck"
                        % (piece, ", ".join(sorted(dead[piece]))))

    # ── Hero coverage ────────────────────────────────────────────────
    for rname in ["StartCardsModified", "CardChestAdditionOverridden",
                  "CardEnergyAdditionOverridden"]:
        cfg = rule(doc, rname)
        if isinstance(cfg, dict):
            missing = HEROES - set(cfg)
            if missing:
                warnings.append("%s does not cover %s" % (rname, ", ".join(sorted(missing))))

    # ── Point values (Point Progressive mode only) ───────────────────
    pts = rule(doc, "DoriathPointLevelUpRule")
    if isinstance(pts, dict):
        required = ["KillEnemy", "HurtEnemy", "HurtBoss", "KillBoss", "LootGold",
                    "LootChest", "LootStand", "OpenDoor", "UnlockDoor", "UseFountain",
                    "RevivePlayer", "Keyholder", "KillHealthDivisor", "PVPisOn",
                    "Points4Minions", "LevelPercentage"]
        for k in required:
            if k not in pts:
                errors.append("point values: key '%s' missing" % k)
        lp = pts.get("LevelPercentage")
        if isinstance(lp, (int, float)):
            if lp <= 0:
                errors.append("LevelPercentage = %s: progression would be impossible" % lp)
            elif lp < 0.5:
                warnings.append("LevelPercentage = %s: gains of 1 raw point will round to 0" % lp)
        div = pts.get("KillHealthDivisor")
        if isinstance(div, int) and div < 0:
            errors.append("negative KillHealthDivisor (%d)" % div)
        p4m = pts.get("Points4Minions")
        if isinstance(p4m, int) and not 0 <= p4m <= 4:
            errors.append("Points4Minions = %d outside 0..4" % p4m)
        # a gain that rounds to 0 never earns anything
        if isinstance(lp, (int, float)) and lp > 0:
            for k in ["HurtEnemy", "KillEnemy", "LootGold", "LootChest", "LootStand",
                      "OpenDoor", "UnlockDoor", "UseFountain", "RevivePlayer"]:
                v = pts.get(k)
                if isinstance(v, (int, float)) and 0 < v and round(v * lp) == 0:
                    warnings.append("%s = %s x %s rounds to 0 points — the action has no effect" % (k, v, lp))

    return errors, warnings


def main(argv):
    targets = argv[1:]
    if not targets:
        here = os.path.dirname(os.path.abspath(__file__))
        targets = sorted(glob.glob(os.path.join(here, "..", "*.json")))
    if not targets:
        print("no file to check")
        return 1

    failed = False
    for path in targets:
        errors, warnings = check(path)
        print("\n== %s" % os.path.basename(path))
        for e in errors:
            print("  ERROR  %s" % e)
        for w in warnings:
            print("  warning  %s" % w)
        if not errors and not warnings:
            print("  all consistent")
        elif not errors:
            print("  no blocking error")
        failed = failed or bool(errors)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
