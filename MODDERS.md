# Clothes Have Pockets - guide for modders

The full modder reference for Clothes Have Pockets. Questions and reports: the Clothes Have Pockets page on mods.vintagestory.at.

## For Modders

Give your own clothing pockets with one small JSON patch - no code, and no dependency entry.

### Quick Start

**1.** **Create the file**

Make a file at `assets/<yourmod>/patches/clotheshavepockets.json` - any file name will do.

**2.** **Copy the example below into it**

Change **file** to your garment's itemtype, and **slots** to how many pockets it should give.

**3.** **Load a world**

Hover the garment in game: its tooltip says how many pockets it has. The server log also counts every garment with pockets - see *Checking It Worked* below.

**Minimal example** - two pockets on one garment:

```json
[
  {
    "op": "addmerge",
    "path": "/attributes",
    "file": "yourmod:itemtypes/wearable/trousers.json",
    "dependsOn": [ { "modid": "clotheshavepockets" } ],
    "value": { "clotheshavepockets": { "slots": 2 } }
  }
]
```

**That is all there is.** Clothes Have Pockets reads one attribute off a garment. Anything that carries it gets pockets, wherever it is worn - nothing in this mod has to know your mod exists.

#### Show the example with a count per variant

For one file that defines many garments through `variantgroups`:

```json
[
  {
    "op": "addmerge",
    "path": "/attributes",
    "file": "yourmod:itemtypes/wearable/lowerbody.json",
    "dependsOn": [ { "modid": "clotheshavepockets" } ],
    "value": {
      "clotheshavepocketsByType": {
        "*-workmans-trousers": { "slots": 3 },
        "@.*-(hose-blue|hose-red|silk-leggings)": { "slots": 1 },
        "*": { "slots": 2 }
      }
    }
  }
]
```

### Field Reference

| **The attribute** |  |
|---|---|
| `clotheshavepockets` | The whole configuration: `{ "slots": <number> }` under the garment's `attributes`. |
| `slots` | How many pockets the garment gives, up to 10. `0` means none - no slots and no tooltip line. |
| `clotheshavepocketsByType` | The same, per variant, for a file with `variantgroups`. Resolved when the game loads, so one entry covers every variant. |

Selectors in `clotheshavepocketsByType`:

| `"*-name"` | Matches one variant, by the end of its code. |
|---|---|
| `"@.*-(a\|b\|c)"` | A regular expression, for a group of variants. |
| `"*"` | Everything else. **Put it last** - the first match wins, so a `*` placed earlier swallows everything below it. |

- Selectors are matched against the full item code, for example `clothes-lowerbody-silk-leggings`. It is the same syntax the game uses for `shapeByType` and `textureByType`, so if your file has one of those you can copy its selectors.

- **Use `addmerge`, never `add`.** `add` replaces the garment's whole `attributes` block, the game's own entries included. `addmerge` creates the key if it is missing and merges alongside anything already there.

### Zero Is A Real Answer

`"slots": 0` says the garment has no pockets, on purpose. Use it for things that plainly would not have any - a cape, a scarf, a grass skirt - rather than leaving them unpatched, so the next person reading your patch can see the decision was made.

### Limits

| **10 pockets per garment** | More than that is capped at 10, with a warning in the server log naming the garment. |
|---|---|
| **Character slots 0 to 19** | A garment worn in a clothing slot past that gets no pockets, again with a warning naming it. |

Neither is likely to come up, and both say so in the log rather than failing quietly.

### Which Slots Make Sense

Any clothing slot works. For what it is worth, the patches this mod ships give two pockets to trousers and outer body clothing, one to aprons, none to most belts - but a working belt gives one and an alchemist's belt four - and none to capes, scarves, robes, hoods, jewellery or grass pants. It is your clothing; you are free to disagree.

### Checking It Worked

**This is the part worth doing.** A patch with a wrong `file` path is skipped in silence, and in game that looks exactly like a patch that ran and did nothing.

**1.** Start the server and look for this line:

```
[clotheshavepockets] Server: N item(s) declare pockets, 0 already had the tooltip behaviour.
```

Note **N** before your patch and again after. It should go up by the number of garments you patched - counting every variant, not every file.

**2.** Hover the garment in game. The tooltip says how many pockets it has, and says nothing at all if the count is 0.

If **N** did not move, the patch did not land. The usual cause is the `file` path: check the domain matches the folder your assets really live in under `assets/`, which is not always your mod's name.

### Shipping It With Your Mod

**Do not add Clothes Have Pockets to your dependencies.** Ship the patch unconditionally, with its `dependsOn` line. For anyone who does not have Clothes Have Pockets the patch is skipped silently - no warnings, no errors. A dependency entry would do the opposite and stop your mod loading without it.

**Keep the `dependsOn` guard.** Without it, players who do not have Clothes Have Pockets get a patch in their log for a mod they never asked for.

**Patching someone else's clothing?** List both mods: `"dependsOn": [ { "modid": "clotheshavepockets" }, { "modid": "<their mod>" } ]`, so the patch only applies when both are installed. This is how SimpleCloth Compatibility Patches gives pockets to SimpleCloth Updated - a separate mod, with nothing here changed for it.
