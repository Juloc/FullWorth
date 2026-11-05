# Receipt vision bench

Measures the image code of the receipt camera on **synthetic photos with a known answer**, and tunes it.
Nothing here is part of the app or the Docker image. It needs only Node (no browser, no packages).

The code under test is the browser code itself - `wwwroot/pages/purchases/receipt-vision.js` (find the receipt,
straighten it) and `receipt-stitch.js` (join the parts of a long receipt). `load.mjs` hands node temporary `.mjs`
copies of them; the files stay as they are.

```bash
node ops/receipt-vision-bench/check.mjs                  # ~15 s: has anything got worse than measured? (exit code 1 if so)
node ops/receipt-vision-bench/sheet.mjs sheet.png        # a contact sheet: one scene per category, true outline in green
```

## Scenes

`scene-lib.mjs` draws a receipt (lines of pseudo-text, separators, prices, a thermal-print tone) on a table with a pinhole
camera: focal length, tilt forward/back and sideways, rotation. Every pixel is traced back onto the paper, so the four
corners of the paper are the exact answer. A scene is a function of (category, seed) and can always be made again.

Categories: `easy skew rotate crumple lightdesk pattern shadow glare occluded clutter small cut lowlight blurry nosheet`
- `skew` strong tilt (to 38°), `rotate` rotation to 80°, `crumple` wavy paper with creases and shading,
  `lightdesk` white or marble table (the receipt is barely lighter than what it lies on), `pattern` gingham or keyboard,
  `shadow` a hand's shadow across half the picture, `glare` a bright spot, `occluded` a finger over an edge,
  `clutter` other sheets on the table, `small` a receipt far away, `cut` a long receipt running past the picture edge,
  `lowlight`, `blurry` (box or motion blur), `nosheet` no receipt at all (any answer is a false alarm).

## Detection

```bash
node ops/receipt-vision-bench/bench-detect.mjs                                   # all sources, seeds 1-12
node ops/receipt-vision-bench/bench-detect.mjs --seeds 1001-1012                 # hold-out set: never used for tuning
node ops/receipt-vision-bench/bench-detect.mjs --det helligkeit,textur,kanten,alle --cats crumple,skew
node ops/receipt-vision-bench/bench-detect.mjs --fails dir                       # pictures of the misses (green truth, red found)
```

*good* = the outline covers the true paper with IoU >= 0.93, *ok* >= 0.85, *wrong* = something found but not the
paper, *missed* = nothing found. For `nosheet`: how many scenes got an outline anyway.

The camera finds candidate quadrilaterals from three sources (brightest region, region full of text, four long straight
edges) and rates each by how much its sides are real edges, how much brighter and more textured its inside is than its
surroundings, its size and how many sides lie on the picture border; then each side snaps to the strongest edge near it.
A source alone is much worse than the three together (hold-out 2001-2012, 168 scenes with a receipt):

| sources | good | ok | wrong | missed |
|---|---|---|---|---|
| brightness only | 104 | 31 | 16 | 17 |
| edges only | 102 | 19 | 16 | 31 |
| all three (the camera) | 120 | 32 | 4 | 12 |

The old brightness-only detector (before this was built) found 75 of 140 scenes cleanly. On a final hold-out set
(3001-3012) the camera gets 117 good + 34 ok of 168, 5 wrong, 12 missed (all of them `lightdesk`: the receipt on a white
table) and no false alarm on the 12 scenes without a receipt. Missing is on purpose: below the threshold the camera shows
no outline and the shutter stays manual - a wrong outline would crop a receipt.

### Fitting the weights

The rating is a logistic model on the candidate features (`MODEL` in `receipt-vision.js`).

```bash
node ops/receipt-vision-bench/learn.mjs dump --seeds 101-122 --out dump.json      # all candidates of all scenes, with their IoU
node ops/receipt-vision-bench/learn.mjs fit dump.json --emit model.json           # fit, pick the threshold, print MODEL
node ops/receipt-vision-bench/bench-detect.mjs --seeds 1001-1012 --model model.json   # on scenes it has not seen
```

## Long receipts

```bash
node ops/receipt-vision-bench/bench-stitch.mjs                        # seeds 1-20
node ops/receipt-vision-bench/bench-stitch.mjs --seeds 1001-1020 --shake 2 --dump rows.json --save dir
```

A long receipt (230-630 mm) is "driven" from top to bottom in frames that overlap by half to two thirds, each with some
hand shake (tilt, rotation, scale) and sometimes motion blur. Every frame is detected, straightened (`stripCorners`) and
joined to the next by the vertical shift that makes their overlap correlate best. The true overlap is known from the
geometry.

`splitIntoPages` then cuts a joined image into pages of at most 1 : 1.6 at the quietest rows, without overlap (`check.mjs`
asserts that the pages add up to exactly the joined image). A tall image would be scaled down for the analysis until the
print is unreadable; pages keep it.

A seam is accepted only if it correlates well **and** is unambiguous (no second, almost equally good place: separators and
blank lines look the same everywhere). Where a seam is not accepted the receipt stays in separate parts. Hold-out 1001-1020:
277 seams, 250 accepted - none wrong, mean error 0.85 px of 360 (greatest 3.5) - 3 rightly refused, 24 refused although
they would have been right. With stronger shake (`--shake 2`) 55 % of the seams are accepted and 2 are off by 6-7 px (a
quarter of a text line, no duplicated or lost line). The wrong ones that mattered during development were *confident* ones:
a single separator line matches itself anywhere, so overlaps shorter than a quarter of the strip are not considered.

## Known limit

A hand's shadow across the paper can make the detector take the shadow's edge for a side, so the outline covers only part
of the receipt (2 of 130 scenes in a recall check; they rate like good results, a text-outside-the-outline measure did not
separate them from good results on textured tables either and was dropped). The camera shows the outline before it takes
the photo; the scan set shows a straightened photo and has "Original" one tap away.

## What this does not say

The numbers are about what the generator can draw. Real receipts, lenses, lighting and hands are different. A photo that
fails in practice is best turned into a new kind of scene in `scene-lib.mjs` so that it is measured from then on.
