# Camera Property Optimization — Integration Guide

Integration documentation for websites communicating with the DUVC API to
analyze a cropped ROI image and set camera properties to optimal values.
The ROI size is not fixed — it is determined by the frontend and passed
as ImageData of any dimension to the analysis functions.

All algorithms are generic: they use the capabilities endpoint to discover
property ranges at runtime, so they work with any UVC camera.

---

## Table of Contents

1. [API Reference](#1-api-reference)
2. [Property Domains](#2-property-domains)
3. [Optimization Order](#3-optimization-order)
4. [Shared Utilities](#4-shared-utilities)
5. [Focus Analysis](#5-focus-analysis)
6. [Exposure Analysis](#6-exposure-analysis)
7. [Gain Analysis](#7-gain-analysis)
8. [Brightness Analysis](#8-brightness-analysis)
9. [Contrast Analysis](#9-contrast-analysis)
10. [White Balance Analysis](#10-white-balance-analysis)
11. [Sharpness Analysis](#11-sharpness-analysis)
12. [Saturation Analysis](#12-saturation-analysis)
13. [Gamma Analysis](#13-gamma-analysis)
14. [Hue Analysis](#14-hue-analysis)
15. [Backlight Compensation Analysis](#15-backlight-compensation-analysis)
16. [Full Optimization Pipeline](#16-full-optimization-pipeline)

---

## 1. API Reference

Base URL: `http://127.0.0.1:3790` (configurable via `DUVC_API_PORT`).

### Discover capabilities (call on page load)

```
GET /api/usb-camera/capabilities
```

Response — `output` field contains lines like:

```
CAM Exposure: [-13,0] step=1 default=-6 current=-6 (AUTO)
VID Brightness: [-64,64] step=1 default=0 current=0 (MANUAL)
```

### Set a property

```http
POST /api/usb-camera/set
Content-Type: application/json

{ "domain": "cam", "property": "Exposure", "value": "-4" }
```

### Set mode (auto/manual)

```http
POST /api/usb-camera/set
Content-Type: application/json

{ "domain": "cam", "property": "Exposure", "mode": "auto" }
```

### Get current values

```http
POST /api/usb-camera/get
Content-Type: application/json

{ "domain": "cam", "properties": ["Focus","Exposure"], "json": true }
```

### Reset to default

```http
POST /api/usb-camera/reset
Content-Type: application/json

{ "domain": "vid", "property": "Brightness" }
```

### WebSocket (real-time updates)

```javascript
const ws = new WebSocket("ws://127.0.0.1:3790/ws");
ws.onmessage = (e) => console.log(JSON.parse(e.data));
```

---

## 2. Property Domains

| Domain | Interface          | What it controls                                      |
|--------|--------------------|-------------------------------------------------------|
| `cam`  | CameraControl      | Physical hardware: lens (Focus), shutter (Exposure), gimbal (Pan/Tilt/Zoom) |
| `vid`  | VideoProcAmp       | ISP post-capture: Brightness, Contrast, Gain, WhiteBalance, Sharpness, etc. |

**CAM and VID are independent at the API level.** Setting one does not reset
the other. However, they affect the same visual output so there are practical
interactions — see the optimization order below.

---

## 3. Optimization Order

Properties must be tuned in a specific order because earlier settings affect
the image that later algorithms analyze. Where properties are independent,
they run in **parallel** on the same captured frame to reduce total time.

**Image source:** USB cameras often apply different ISP processing (color,
gain, sharpening) at different resolutions, so VID property analysis must
use **snapped ROI images** at the target capture resolution. Focus and
Exposure are physical hardware controls (lens motor, shutter) that behave
identically regardless of resolution, so Focus uses the **video feed** for
speed (hill-climbing needs rapid iterations), while Exposure uses snaps
for accuracy.

```
Step 1: Focus ═══════════ Exposure           PARALLEL (CAM hardware)
        │  Focus: video feed ROI (fast hill-climbing, up to 30 iterations)
        │  Exposure: snapped ROI (iterative, up to 5 snaps)
        ▼  wait for both to converge
Step 2: Gain ═══════════ BacklightComp       PARALLEL (single snap, independent)
        │  Gain = signal amplification
        │  BLC = metering bias (only in auto-exposure mode)
        ▼  apply both, snap new frame
Step 3: Gamma                                SEQUENTIAL
        ▼  reshapes tone curve -> shifts what brightness sees
Step 4: Brightness                           SEQUENTIAL
        ▼  shifts histogram center -> changes what contrast sees
Step 5: Contrast                             SEQUENTIAL
        ▼  spreads histogram -> affects color measurements
Step 6: WhiteBalance                         SEQUENTIAL (iterative, up to 3 snaps)
        ▼  changes color balance -> affects saturation/hue
Step 7: Saturation ══════ Hue               PARALLEL (single snap, independent axes)
        │  Saturation = chroma magnitude
        │  Hue = chroma angle rotation
        ▼  apply both, snap new frame
Step 8: Sharpness                            SEQUENTIAL (must be last)
```

~12-16 snaps + ~15-30 video frames for focus. All analyzing the ROI.

**Why this order:**
- Focus and Exposure are independent CAM hardware controls (lens motor vs
  shutter). They can converge simultaneously. Focus uses the video feed
  for rapid hill-climbing; Exposure uses snaps for ISP-accurate analysis.
- Exposure determines how many photons reach the sensor — everything else
  depends on the signal it captures.
- Gain amplifies signal + noise; only use it after maximizing exposure.
- Gamma/Brightness/Contrast reshape the tonal curve and must happen before
  color analysis (color metrics shift with brightness). They chain: gamma
  changes the curve, brightness reads the new median, contrast expands
  around the new center.
- White Balance must be set before Saturation (chroma scaling is meaningless
  on a color-casted image).
- Saturation and Hue operate on independent color axes (chroma magnitude
  vs chroma angle) — neither affects the other's measurement.
- Sharpness is last because every other adjustment changes the frequency
  content the sharpness algorithm measures.

**Between each step**, wait 200-500ms for the camera ISP to settle, then
snap a fresh ROI frame for the next analysis.

---

## 4. Shared Utilities

### Parse capabilities into a lookup

```javascript
/**
 * Parse the capabilities output string into a Map keyed by "domain.Property".
 * @param {string} output - Raw output from GET /api/usb-camera/capabilities
 * @returns {Map<string, {domain, name, min, max, step, default, current, mode}>}
 */
function parseCapabilities(output) {
  const caps = new Map();
  const re = /^\s*(CAM|VID)\s+([^:]+):\s+\[(-?\d+),(-?\d+)\]\s+step=(-?\d+)\s+default=(-?\d+)\s+current=(-?\d+)\s+\(([^)]+)\)/gm;
  let m;
  while ((m = re.exec(output)) !== null) {
    const domain = m[1].toLowerCase();
    const name   = m[2].trim();
    caps.set(`${domain}.${name}`, {
      domain, name,
      min:     parseInt(m[3], 10),
      max:     parseInt(m[4], 10),
      step:    parseInt(m[5], 10),
      default: parseInt(m[6], 10),
      current: parseInt(m[7], 10),
      mode:    m[8].trim().toLowerCase(),
    });
  }
  return caps;
}
```

### API helper

```javascript
const API_BASE = "http://127.0.0.1:3790";

async function setProperty(domain, property, value) {
  const res = await fetch(`${API_BASE}/api/usb-camera/set`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ domain, property, value: String(value) }),
  });
  return res.json();
}

async function setMode(domain, property, mode) {
  const res = await fetch(`${API_BASE}/api/usb-camera/set`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ domain, property, mode }),
  });
  return res.json();
}

async function getCapabilities() {
  const res = await fetch(`${API_BASE}/api/usb-camera/capabilities`);
  const json = await res.json();
  return parseCapabilities(json.output);
}
```

### Luminance computation (BT.709)

```javascript
/** Compute per-pixel luminance array from RGBA ImageData. */
function toLuminance(imageData) {
  const { data, width, height } = imageData;
  const n = width * height;
  const lum = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const o = i * 4;
    lum[i] = 0.2126 * data[o] + 0.7152 * data[o + 1] + 0.0722 * data[o + 2];
  }
  return lum;
}
```

### Luminance histogram

```javascript
/** Build a 256-bin histogram from a luminance array. */
function buildHistogram(lum) {
  const hist = new Uint32Array(256);
  for (let i = 0; i < lum.length; i++) {
    hist[Math.max(0, Math.min(255, Math.round(lum[i])))]++;
  }
  return hist;
}

/** Get a percentile value from a histogram. */
function histPercentile(hist, total, pct) {
  const target = Math.floor(total * pct);
  let cumulative = 0;
  for (let i = 0; i < 256; i++) {
    cumulative += hist[i];
    if (cumulative >= target) return i;
  }
  return 255;
}
```

### Clamp to camera range

```javascript
/** Clamp and round a value to the camera's property range and step. */
function clampToRange(value, cap) {
  const stepped = Math.round(value / cap.step) * cap.step;
  return Math.max(cap.min, Math.min(cap.max, stepped));
}
```

---

## 5. Focus Analysis

Camera range: `[0, 990]`, step=1, default=68. Supports auto/manual.

Focus controls the lens motor position. It determines optical sharpness —
no amount of ISP sharpening (VID Sharpness) can recover a misfocused image.

Focus runs on the **video feed** (not snapped images) for speed, since
hill-climbing needs rapid iterations. It analyzes only the ROI to find the
focal position where that specific region is sharpest.

### Algorithm

Uses Laplacian variance as a focus metric in a hill-climbing loop:

1. Compute Laplacian variance of the ROI crop from the video feed.
2. Compare to the previous frame's score.
3. Step focus in the direction that increases the score.
4. When score decreases, reverse direction with a smaller step.
5. Converge when step size reaches minimum and score stops improving.

### Code

```javascript
/**
 * Analyze focus quality from an ROI image.
 * Returns a focus sharpness score (Laplacian variance).
 * Higher = sharper = better focused.
 *
 * @param {ImageData} imageData - ROI crop (any size)
 * @returns {{ score: number, metrics: object }}
 */
function analyzeFocus(imageData) {
  const { width, height } = imageData;
  const lum = toLuminance(imageData);

  const margin = 32;
  let lapSum = 0, lapSqSum = 0, count = 0;

  for (let y = margin; y < height - margin; y++) {
    for (let x = margin; x < width - margin; x++) {
      const idx = y * width + x;
      const lap = lum[idx - width] + lum[idx + width] +
                  lum[idx - 1] + lum[idx + 1] - 4 * lum[idx];
      lapSum += lap;
      lapSqSum += lap * lap;
      count++;
    }
  }

  const lapMean = lapSum / count;
  const lapVar = (lapSqSum / count) - lapMean * lapMean;

  return {
    score: lapVar,
    metrics: {
      laplacianVariance: +lapVar.toFixed(2),
      pixelsAnalyzed: count,
    },
  };
}

/**
 * Hill-climbing focus controller.
 * Call update() with each new frame. It drives the focus motor toward
 * the position that maximizes Laplacian variance in the ROI.
 *
 * Uses the video feed for speed — hill-climbing needs rapid iterations.
 * Analyzes only the ROI crop so focus is optimized for the selected
 * region, not the full sensor.
 *
 * @param {number} initialFocus - Starting focus position
 * @param {object} cap - Focus capability from parseCapabilities()
 * @param {object} [opts]
 * @param {number} [opts.initialStep=40] - Initial step size
 * @param {number} [opts.minStep=1] - Minimum step before convergence
 * @param {number} [opts.settleFrames=2] - Frames to skip after motor move
 */
function createFocusController(initialFocus, cap, opts = {}) {
  const {
    initialStep = 40,
    minStep = 1,
    settleFrames = 2,
  } = opts;

  let current = initialFocus;
  let bestScore = -1;
  let bestPos = current;
  let stepSize = initialStep;
  let direction = 1;
  let framesSinceMove = settleFrames;
  let converged = false;
  let phase = "coarse";

  return {
    get current() { return current; },
    get converged() { return converged; },

    /**
     * @param {ImageData} imageData - ROI crop from video feed (any size)
     * @returns {{ status, recommendedFocus, delta, score }}
     */
    update(imageData) {
      if (converged) {
        return {
          status: "converged",
          recommendedFocus: bestPos,
          delta: 0,
          score: bestScore,
        };
      }

      if (framesSinceMove < settleFrames) {
        framesSinceMove++;
        return {
          status: "settling",
          recommendedFocus: current,
          delta: 0,
          score: -1,
        };
      }

      const { score } = analyzeFocus(imageData);

      if (score > bestScore) {
        bestScore = score;
        bestPos = current;
        const next = clampToRange(current + direction * stepSize, cap);

        if (next === current) {
          direction *= -1;
          stepSize = Math.max(minStep, Math.floor(stepSize / 2));
          if (stepSize <= minStep) {
            converged = true;
            phase = "converged";
            return {
              status: "converged", recommendedFocus: bestPos,
              delta: 0, score,
            };
          }
        }

        current = next;
        framesSinceMove = 0;
        return {
          status: phase === "coarse" ? "searching_coarse" : "searching_fine",
          recommendedFocus: current,
          delta: current - bestPos,
          score,
        };
      } else {
        direction *= -1;
        stepSize = Math.max(minStep, Math.floor(stepSize / 2));

        if (stepSize <= minStep && phase === "fine") {
          converged = true;
          phase = "converged";
          current = bestPos;
          return {
            status: "converged", recommendedFocus: bestPos,
            delta: 0, score: bestScore,
          };
        }

        if (phase === "coarse" && stepSize <= 5) {
          phase = "fine";
        }

        current = clampToRange(bestPos + direction * stepSize, cap);
        framesSinceMove = 0;
        return {
          status: phase === "coarse" ? "searching_coarse" : "searching_fine",
          recommendedFocus: current,
          delta: current - bestPos,
          score,
        };
      }
    },

    reset(focus) {
      current = focus;
      bestScore = -1;
      bestPos = current;
      stepSize = initialStep;
      direction = 1;
      framesSinceMove = settleFrames;
      converged = false;
      phase = "coarse";
    },
  };
}
```

---

## 6. Exposure Analysis

Camera range: `[-13, 0]`, step=1. Values are power-of-2 exponents
(`2^val` seconds). Each step = 1 EV (one photographic stop).

### Algorithm

1. Compute center-weighted mean luminance (center 50% of frame gets 2x weight).
2. Compute highlight clipping (% pixels >= 250) and shadow clipping (% pixels <= 5).
3. Detect HDR scenes (simultaneous highlight + shadow clipping).
4. Calculate correction in EV stops: `correction = log2(target / meanBrightness)`.
5. Apply dampening (0.6x) and clamp per-frame to +-3 steps.

### Code

```javascript
/**
 * Analyze exposure from an ROI image.
 *
 * @param {ImageData} imageData
 * @param {number}    currentExposure - Current camera value (e.g. -6)
 * @param {object}    cap - Capability object from parseCapabilities()
 * @param {object}    [opts]
 * @param {number}    [opts.targetBrightness=128] - Target mean luminance
 * @param {number}    [opts.dampening=0.6]
 * @param {number}    [opts.maxStepPerFrame=3]
 * @returns {{ status, recommendedExposure, stepsToApply, metrics }}
 */
function analyzeExposure(imageData, currentExposure, cap, opts = {}) {
  const {
    targetBrightness = 128,
    dampening = 0.6,
    maxStepPerFrame = 3,
    deadZone = 8,
    highlightClipLevel = 250,
    shadowClipLevel = 5,
    maxHighlightClipPct = 1.0,
    maxShadowClipPct = 3.0,
  } = opts;

  const { data, width, height } = imageData;
  const totalPixels = width * height;
  const lum = toLuminance(imageData);
  const hist = buildHistogram(lum);

  // Center-weighted mean
  const cx = width / 2, cy = height / 2;
  const cwRadius = 0.5 * Math.sqrt(cx * cx + cy * cy);
  let wSum = 0, wTotal = 0;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const dist = Math.sqrt((x - cx) ** 2 + (y - cy) ** 2);
      const w = dist <= cwRadius ? 2.0 : 1.0;
      wSum += lum[y * width + x] * w;
      wTotal += w;
    }
  }
  const meanBrightness = wSum / wTotal;

  // Clipping
  let hlClip = 0, shClip = 0;
  for (let i = highlightClipLevel; i < 256; i++) hlClip += hist[i];
  for (let i = 0; i <= shadowClipLevel; i++) shClip += hist[i];
  const hlPct = (hlClip / totalPixels) * 100;
  const shPct = (shClip / totalPixels) * 100;

  // HDR detection
  const isHDR = hlPct > 0.5 && shPct > 2.0;

  // Correction
  const effectiveMean = Math.max(1, meanBrightness);
  const target = isHDR ? targetBrightness * 0.7 : targetBrightness;
  let correctionEV = 0;
  let reason = "Exposure is within acceptable range";

  if (hlPct > maxHighlightClipPct) {
    correctionEV = Math.min(Math.log2(target / effectiveMean), -0.5);
    reason = `Highlights clipping at ${hlPct.toFixed(1)}%`;
  } else if (Math.abs(effectiveMean - target) > deadZone) {
    correctionEV = Math.log2(target / effectiveMean);
    reason = effectiveMean < target ? "Underexposed" : "Overexposed";
  }

  let steps = Math.round(correctionEV * dampening);
  steps = Math.max(-maxStepPerFrame, Math.min(maxStepPerFrame, steps));
  const recommended = clampToRange(currentExposure + steps, cap);
  steps = recommended - currentExposure;

  return {
    status: steps === 0 ? "optimal" : steps > 0 ? "underexposed" : "overexposed",
    reason,
    recommendedExposure: recommended,
    stepsToApply: steps,
    metrics: {
      meanBrightness: Math.round(meanBrightness * 10) / 10,
      highlightClipPct: Math.round(hlPct * 100) / 100,
      shadowClipPct: Math.round(shPct * 100) / 100,
      isHDR,
    },
  };
}
```

### Convergence controller

For continuous auto-exposure, wrap the analyzer in a stateful controller
that tracks settling time and detects oscillation:

```javascript
function createExposureController(initialExposure, cap, opts = {}) {
  const settleFrames = opts.settleFrames || 3;
  let current = initialExposure;
  let framesSinceChange = settleFrames;
  let lastDir = 0;
  let oscillations = 0;
  let effectiveDeadZone = opts.deadZone || 8;

  return {
    get current() { return current; },

    update(imageData) {
      if (framesSinceChange < settleFrames) {
        framesSinceChange++;
        return { status: "settling", stepsToApply: 0, recommendedExposure: current };
      }

      const result = analyzeExposure(
        imageData, current, cap, { ...opts, deadZone: effectiveDeadZone }
      );

      if (result.stepsToApply !== 0) {
        const dir = result.stepsToApply > 0 ? 1 : -1;
        if (dir === -lastDir && lastDir !== 0) {
          oscillations++;
          if (oscillations >= 2) {
            effectiveDeadZone = Math.min(effectiveDeadZone + 4, 24);
            const retry = analyzeExposure(
              imageData, current, cap, { ...opts, deadZone: effectiveDeadZone }
            );
            if (retry.stepsToApply === 0) return retry;
          }
        } else {
          oscillations = Math.max(0, oscillations - 1);
          effectiveDeadZone = Math.max(opts.deadZone || 8, effectiveDeadZone - 1);
        }
        lastDir = dir;
        current = result.recommendedExposure;
        framesSinceChange = 0;
      }
      return result;
    },

    reset(exposure) {
      current = exposure;
      framesSinceChange = settleFrames;
      lastDir = 0;
      oscillations = 0;
    },
  };
}
```

---

## 7. Gain Analysis

Camera range: `[0, 128]`, step=1, default=70.
Gain amplifies the sensor signal — increases brightness but also noise.

**Rule: maximize exposure before increasing gain.** Gain is a last resort.

### Algorithm

1. Compute luminance mean and noise sigma using two methods:
   - **Laplacian variance**: convolve with `[0,1,0; 1,-4,1; 0,1,0]`, compute
     variance, derive `sigma = sqrt(variance / 20)`.
   - **Local variance of flat blocks**: divide into 16x16 blocks, take the
     lowest-variance 10% (flat regions), average their variance.
2. Combine: `sigma = 0.6 * localSigma + 0.4 * laplacianSigma`.
3. Estimate SNR: `20 * log10(meanBrightness / sigma)`.
4. Decision: if noisy and exposure has headroom -> decrease gain, advise
   increasing exposure. If dark and exposure is maxed -> increase gain.

### Noise reference (0-255 scale)

| Sigma | Quality |
|-------|---------|
| 1-3   | Very clean |
| 3-8   | Normal USB camera |
| 8-14  | Noticeably noisy |
| 14-22 | Objectionably noisy |
| 22+   | Severe degradation |

### Code

```javascript
/**
 * @param {ImageData} imageData
 * @param {number}    currentGain
 * @param {number}    currentExposure - Current exposure value
 * @param {object}    capGain - Gain capability
 * @param {object}    capExposure - Exposure capability
 */
function analyzeGain(imageData, currentGain, currentExposure, capGain, capExposure) {
  const { width, height } = imageData;
  const lum = toLuminance(imageData);
  const n = width * height;

  // Mean brightness
  let sum = 0;
  for (let i = 0; i < n; i++) sum += lum[i];
  const mean = sum / n;

  // --- Laplacian noise estimate ---
  let lapSum = 0, lapSqSum = 0, lapCount = 0;
  for (let y = 1; y < height - 1; y++) {
    for (let x = 1; x < width - 1; x++) {
      const idx = y * width + x;
      const lap = lum[idx - width] + lum[idx + width] +
                  lum[idx - 1] + lum[idx + 1] - 4 * lum[idx];
      lapSum += lap;
      lapSqSum += lap * lap;
      lapCount++;
    }
  }
  const lapMean = lapSum / lapCount;
  const lapVar = (lapSqSum / lapCount) - lapMean * lapMean;
  const sigmaLap = Math.sqrt(Math.max(0, lapVar) / 20);

  // --- Local variance of flat 16x16 blocks ---
  const bs = 16;
  const variances = [];
  for (let by = 0; by < Math.floor(height / bs); by++) {
    for (let bx = 0; bx < Math.floor(width / bs); bx++) {
      let s = 0, sq = 0;
      for (let dy = 0; dy < bs; dy++) {
        for (let dx = 0; dx < bs; dx++) {
          const v = lum[(by * bs + dy) * width + bx * bs + dx];
          s += v; sq += v * v;
        }
      }
      const cnt = bs * bs;
      const bMean = s / cnt;
      if (bMean > 15 && bMean < 240) {
        variances.push((sq / cnt) - bMean * bMean);
      }
    }
  }
  variances.sort((a, b) => a - b);
  const numFlat = Math.max(1, Math.floor(variances.length * 0.1));
  let flatSum = 0;
  for (let i = 0; i < numFlat; i++) flatSum += variances[i];
  const sigmaLocal = Math.sqrt(Math.max(0, flatSum / numFlat));

  // Combined sigma
  const sigma = sigmaLocal > 0 && sigmaLap > 0
    ? 0.6 * sigmaLocal + 0.4 * sigmaLap
    : sigmaLocal || sigmaLap;

  const snr = sigma > 0.5 ? 20 * Math.log10(Math.max(mean, 1) / sigma) : 60;

  // Exposure headroom: can we increase exposure instead of gain?
  const exposureHasHeadroom = currentExposure < capExposure.max * 0.85;

  // Decision
  const isNoisy = sigma > 8;
  const isVeryNoisy = sigma > 14;
  const isDark = mean < 80;
  let action = "maintain";
  let delta = 0;
  let reason = `Image quality good (sigma=${sigma.toFixed(1)}, SNR=${snr.toFixed(1)}dB)`;
  let exposureAdvice = "No changes needed";

  if (isVeryNoisy) {
    delta = -Math.min(currentGain - capGain.min, Math.round((sigma - 8) * 1.2));
    action = "decrease";
    reason = `High noise (sigma=${sigma.toFixed(1)}). Reduce gain.`;
    exposureAdvice = exposureHasHeadroom
      ? "Increase exposure to compensate for gain reduction."
      : "Exposure near max. Accept slightly darker image for noise improvement.";
  } else if (isNoisy && !isDark && exposureHasHeadroom) {
    delta = -Math.min(currentGain - capGain.min, Math.round((sigma - 8) * 0.8));
    action = "decrease";
    reason = `Moderate noise (sigma=${sigma.toFixed(1)}). Prefer exposure over gain.`;
    exposureAdvice = "Increase exposure slightly to compensate.";
  } else if (isDark && !isNoisy && !exposureHasHeadroom) {
    const ratio = 80 / Math.max(mean, 1);
    delta = Math.min(
      capGain.max - currentGain,
      Math.round(35 * Math.log2(Math.min(ratio, 4)))
    );
    delta = Math.max(1, delta);
    action = "increase";
    reason = `Dark (mean=${mean.toFixed(0)}) and exposure maxed. Gain increase needed.`;
    exposureAdvice = "Exposure is at maximum. Gain is the only option.";
  } else if (isDark && exposureHasHeadroom) {
    reason = `Dark (mean=${mean.toFixed(0)}) but exposure has headroom. Increase exposure first.`;
    exposureAdvice = "Increase exposure before touching gain.";
  }

  const suggested = clampToRange(currentGain + delta, capGain);

  return {
    action,
    suggestedGain: suggested,
    delta: suggested - currentGain,
    reason,
    exposureAdvice,
    metrics: { mean: Math.round(mean), sigma: +sigma.toFixed(2), snr: +snr.toFixed(1) },
  };
}
```

---

## 8. Brightness Analysis

Camera range: `[-64, 64]`, step=1, default=0.
Brightness is a post-capture offset — it shifts the histogram left/right
without changing its shape. Different from exposure (pre-capture).

### Algorithm

1. Compute luminance histogram.
2. Find the **median** (robust to outliers, unlike mean).
3. Target median: ~125 (perceptual mid-gray in sRGB).
4. Offset needed = `target - median`, dampened by 0.6.
5. Reduce adjustment if already clipping on the side being pushed toward.

### Code

```javascript
function analyzeBrightness(imageData, currentBrightness, cap) {
  const lum = toLuminance(imageData);
  const hist = buildHistogram(lum);
  const n = lum.length;
  const median = histPercentile(hist, n, 0.5);
  const TARGET = 125;
  const DEAD_ZONE = 8;

  // Clipping
  let clipBlack = 0, clipWhite = 0;
  for (let i = 0; i <= 2; i++) clipBlack += hist[i];
  for (let i = 253; i <= 255; i++) clipWhite += hist[i];
  const clipBlackPct = clipBlack / n;
  const clipWhitePct = clipWhite / n;

  const offset = TARGET - median;
  if (Math.abs(offset) <= DEAD_ZONE) {
    return {
      status: "optimal",
      recommended: currentBrightness,
      delta: 0,
      reason: `Median ${median} is within target zone.`,
      metrics: { median, clipBlackPct, clipWhitePct },
    };
  }

  let adj = Math.round(offset * 0.6);

  // Reduce if pushing toward existing clipping
  if (adj > 0 && clipWhitePct > 0.02)
    adj = Math.round(adj * Math.max(0.1, 1 - clipWhitePct * 10));
  if (adj < 0 && clipBlackPct > 0.02)
    adj = Math.round(adj * Math.max(0.1, 1 - clipBlackPct * 10));

  const recommended = clampToRange(currentBrightness + adj, cap);

  return {
    status: offset > 0 ? "too_dark" : "too_bright",
    recommended,
    delta: recommended - currentBrightness,
    reason: `Median ${median} -> target ${TARGET}. Adjustment: ${adj}.`,
    metrics: { median, clipBlackPct, clipWhitePct },
  };
}
```

---

## 9. Contrast Analysis

Camera range: `[0, 100]`, step=1, default=30.
Contrast scales deviation from the mean: `output = mean + gain * (pixel - mean)`.

### Algorithm

1. Compute RMS contrast: `stddev(luminance) / mean(luminance)`.
2. Compute histogram spread: `(P95 - P5) / 255`.
3. Targets: RMS ~0.25, spread ~70%.
4. Estimate adjustment proportionally (1 contrast unit ~ 0.7% spread change).

**Set brightness first**, then contrast — contrast expands around the current
center point.

### Code

```javascript
function analyzeContrast(imageData, currentContrast, cap) {
  const lum = toLuminance(imageData);
  const hist = buildHistogram(lum);
  const n = lum.length;

  let sum = 0;
  for (let i = 0; i < n; i++) sum += lum[i];
  const mean = sum / n;
  if (mean < 1)
    return { status: "too_dark", recommended: currentContrast, delta: 0 };

  let sqDiff = 0;
  for (let i = 0; i < n; i++) sqDiff += (lum[i] - mean) ** 2;
  const stddev = Math.sqrt(sqDiff / n);
  const rms = stddev / mean;

  const p5 = histPercentile(hist, n, 0.05);
  const p95 = histPercentile(hist, n, 0.95);
  const spread = (p95 - p5) / 255;

  const TARGET_SPREAD = 0.70;
  const SPREAD_DEAD_ZONE = 0.08;
  const SPREAD_PER_UNIT = 0.007;
  const DAMPENING = 0.5;

  const error = TARGET_SPREAD - spread;
  if (Math.abs(error) < SPREAD_DEAD_ZONE) {
    return {
      status: "optimal",
      recommended: currentContrast,
      delta: 0,
      reason: `Spread ${(spread * 100).toFixed(0)}% is within target zone.`,
      metrics: {
        rms: +rms.toFixed(3), stddev: +stddev.toFixed(1),
        spread: +(spread * 100).toFixed(0),
      },
    };
  }

  // Clipping guard
  let clipBlack = 0, clipWhite = 0;
  for (let i = 0; i <= 2; i++) clipBlack += hist[i];
  for (let i = 253; i <= 255; i++) clipWhite += hist[i];
  const totalClip = (clipBlack + clipWhite) / n;

  let adj = Math.round((error / SPREAD_PER_UNIT) * DAMPENING);
  if (adj > 0 && totalClip > 0.03) {
    adj = Math.round(adj * Math.max(0.1, 1 - totalClip * 5));
  }

  const recommended = clampToRange(currentContrast + adj, cap);

  return {
    status: error > 0 ? "low_contrast" : "high_contrast",
    recommended,
    delta: recommended - currentContrast,
    reason: `Spread ${(spread * 100).toFixed(0)}% -> target ${(TARGET_SPREAD * 100).toFixed(0)}%.`,
    metrics: {
      rms: +rms.toFixed(3), stddev: +stddev.toFixed(1),
      spread: +(spread * 100).toFixed(0),
    },
  };
}
```

---

## 10. White Balance Analysis

Camera range: `[2800, 6500]` Kelvin, step=10, default=4600. Supports auto/manual.

### Algorithm: Gray World

The Gray World assumption: the average color of a diverse scene is neutral gray.
If the average R/B ratio deviates from 1.0, the illuminant has a color temperature
bias.

1. Compute average R, G, B of non-extreme pixels (skip lum < 20 or > 235).
2. Compute `ratio = avgR / avgB`.
3. Map ratio to Kelvin via calibrated log curve:
   `T = -4931.4 * ln(ratio) + 5698.7`
4. Clamp to `[2800, 6500]`, round to step=10.

Because the captured frame already has the camera's current WB baked in,
this estimates the **residual** color cast. Use iterative correction:

```
newWB = currentWB + gain * (estimatedTemp - NEUTRAL_TEMP)
```

### When to use auto vs manual WB

| Use Auto | Use Manual |
|----------|-----------|
| Scene dominated by one color | Stable, known lighting |
| Rapidly changing lighting | Need frame-to-frame consistency |
| Low algorithm confidence | Camera auto WB produces poor results |

### Code

```javascript
function analyzeWhiteBalance(imageData, currentWB, cap) {
  const { data, width, height } = imageData;
  const n = width * height;

  let sumR = 0, sumG = 0, sumB = 0, valid = 0;
  for (let i = 0; i < data.length; i += 4) {
    const lum = 0.299 * data[i] + 0.587 * data[i+1] + 0.114 * data[i+2];
    if (lum >= 20 && lum <= 235) {
      sumR += data[i]; sumG += data[i+1]; sumB += data[i+2]; valid++;
    }
  }

  if (valid < n * 0.1) {
    sumR = 0; sumG = 0; sumB = 0; valid = 0;
    for (let i = 0; i < data.length; i += 4) {
      sumR += data[i]; sumG += data[i+1]; sumB += data[i+2]; valid++;
    }
  }

  const avgR = sumR / valid, avgB = sumB / valid;
  const ratio = avgR / Math.max(avgB, 0.01);
  const safeRatio = Math.max(0.5, Math.min(2.5, ratio));

  // Calibrated log mapping (two-point: R/B=1.8->2800K, R/B=0.85->6500K)
  const A = -4931.4, B = 5698.7;
  const estimatedTemp = A * Math.log(safeRatio) + B;

  // Iterative correction: adjust current WB based on residual cast
  const NEUTRAL_TEMP = 5700;
  const gain = 0.5;
  const residual = estimatedTemp - NEUTRAL_TEMP;
  let newWB = currentWB + gain * residual;
  newWB = clampToRange(newWB, cap);

  const castStrength = Math.abs(Math.log(ratio));
  const colorCast = ratio > 1.15 ? "warm" : ratio < 0.95 ? "cool" : "neutral";
  const confidence =
    castStrength > 0.7 ? "low" : castStrength > 0.4 ? "medium" : "high";

  return {
    recommended: newWB,
    delta: newWB - currentWB,
    colorCast,
    confidence,
    preferAuto: confidence === "low",
    reason: confidence === "low"
      ? "Low confidence — possible dominant scene color. Consider auto WB."
      : `Scene appears ${colorCast}. Adjust WB to ${newWB}K.`,
    metrics: {
      avgR: +avgR.toFixed(1), avgB: +avgB.toFixed(1),
      rbRatio: +ratio.toFixed(3), estimatedTemp: Math.round(estimatedTemp),
    },
  };
}
```

---

## 11. Sharpness Analysis

Camera range: `[0, 100]`, step=1, default=90.

### Algorithm

Two complementary metrics detect under-sharpening vs over-sharpening:

1. **Laplacian variance** — primary sharpness score. Higher = sharper.
2. **Halo/overshoot detection** — at strong edges, measure intensity overshoot
   perpendicular to the gradient. Over-sharpening creates bright halos on the
   light side and dark halos on the dark side.
3. **Laplacian kurtosis** — heavy tails indicate halo artifacts (excess
   kurtosis beyond the normal ~3).

### Sharpness vs Focus

Sharpness (ISP post-processing) cannot recover optical blur from misfocus.
If Laplacian variance is very low even at sharpness=100, the lens is out of
focus — adjust Focus first, then re-evaluate Sharpness.

### Code

```javascript
function analyzeSharpness(imageData, currentSharpness, cap) {
  const { width, height } = imageData;
  const lum = toLuminance(imageData);
  const margin = 64; // skip outer pixels (lens edge softness)

  // --- Laplacian variance ---
  const lapValues = [];
  for (let y = margin; y < height - margin; y++) {
    for (let x = margin; x < width - margin; x++) {
      const idx = y * width + x;
      const lap = lum[idx - width] + lum[idx + width] +
                  lum[idx - 1] + lum[idx + 1] - 4 * lum[idx];
      lapValues.push(lap);
    }
  }

  let lapSum = 0, lapSqSum = 0;
  for (let i = 0; i < lapValues.length; i++) {
    lapSum += lapValues[i];
    lapSqSum += lapValues[i] * lapValues[i];
  }
  const lapMean = lapSum / lapValues.length;
  const lapVar = (lapSqSum / lapValues.length) - lapMean * lapMean;

  // --- Kurtosis (halo detection) ---
  let m2 = 0, m4 = 0;
  for (let i = 0; i < lapValues.length; i++) {
    const d = lapValues[i] - lapMean;
    const d2 = d * d;
    m2 += d2;
    m4 += d2 * d2;
  }
  m2 /= lapValues.length;
  m4 /= lapValues.length;
  const kurtosis = m2 > 0 ? m4 / (m2 * m2) : 0;

  // --- Overshoot detection at edges ---
  let overshootCount = 0, edgesChecked = 0;
  const step = 3;
  for (let y = margin + 3; y < height - margin - 3; y += step) {
    for (let x = margin + 3; x < width - margin - 3; x += step) {
      const idx = y * width + x;
      const gx = -lum[idx - width - 1] + lum[idx - width + 1]
               - 2 * lum[idx - 1] + 2 * lum[idx + 1]
               - lum[idx + width - 1] + lum[idx + width + 1];
      const gy = -lum[idx - width - 1] - 2 * lum[idx - width] - lum[idx - width + 1]
               + lum[idx + width - 1] + 2 * lum[idx + width] + lum[idx + width + 1];
      const mag = Math.sqrt(gx * gx + gy * gy);
      if (mag < 45) continue;

      const contrast = Math.abs(lum[idx - 2] - lum[idx + 2]);
      if (contrast < 20) continue;
      const ovPlus = Math.max(0,
        lum[idx + 1] - Math.max(lum[idx + 2], lum[idx + 3])) / contrast;
      const ovMinus = Math.max(0,
        Math.min(lum[idx - 2], lum[idx - 3]) - lum[idx - 1]) / contrast;
      if (Math.max(ovPlus, ovMinus) > 0.02) overshootCount++;
      edgesChecked++;
    }
  }
  const overshootPct = edgesChecked > 0
    ? (overshootCount / edgesChecked) * 100 : 0;

  // --- Recommendation ---
  let assessment, suggested;
  if (lapVar < 50) {
    assessment = "severely_under_sharpened";
    suggested = 65;
  } else if (lapVar < 150) {
    assessment = "under_sharpened";
    suggested = 55;
  } else if (overshootPct > 40 && kurtosis > 8) {
    assessment = "severely_over_sharpened";
    suggested = 30;
  } else if (overshootPct > 25 || kurtosis > 5) {
    assessment = "over_sharpened";
    suggested = 40;
  } else if (overshootPct > 15) {
    assessment = "slightly_over_sharpened";
    suggested = 45;
  } else {
    assessment = "optimal";
    suggested = currentSharpness;
  }

  suggested = clampToRange(suggested, cap);

  return {
    assessment,
    recommended: suggested,
    delta: suggested - currentSharpness,
    reason: `Lap.var=${lapVar.toFixed(0)}, kurtosis=${kurtosis.toFixed(1)}, ` +
            `overshoot=${overshootPct.toFixed(0)}% of edges.`,
    metrics: {
      laplacianVariance: +lapVar.toFixed(1),
      kurtosis: +kurtosis.toFixed(2),
      overshootPct: +overshootPct.toFixed(1),
    },
  };
}
```

### Optional calibration sweep

For best results, sweep sharpness 0-100 in steps of 5, capture a frame at
each setting, and find the knee where overshoot exceeds 15%:

```javascript
async function calibrateSharpness(cap) {
  let optimal = 50;
  for (let s = 0; s <= 100; s += 5) {
    await setProperty("vid", "Sharpness", s);
    await sleep(200);
    const frame = await captureFrame();
    const result = analyzeSharpness(frame, s, cap);
    if (result.metrics.overshootPct < 15) {
      optimal = s;
    } else {
      break;
    }
  }
  await setProperty("vid", "Sharpness", optimal);
  return optimal;
}
```

---

## 12. Saturation Analysis

Camera range: `[0, 128]`, step=1, default=54.
54 is the 1.0x reference (no modification). 0 = grayscale, 128 ~ 2.37x boost.

### Algorithm

1. Convert each pixel to HSV. Skip near-black (V < 0.05) and near-white (V > 0.95).
2. Compute mean saturation of valid pixels.
3. Target mean S: 0.30-0.40 for natural scenes.
4. Detect oversaturation: > 5% pixels at S > 0.95, or channel clipping.
5. Scale recommendation relative to default: `recommended = default * (target / meanS)`.

### Code

```javascript
function analyzeSaturation(imageData, currentSaturation, cap) {
  const { data, width, height } = imageData;
  const n = width * height;
  const DEFAULT_1X = cap.default;

  let totalS = 0, count = 0, oversat = 0, undersat = 0;

  for (let i = 0; i < n; i++) {
    const o = i * 4;
    const r = data[o] / 255, g = data[o+1] / 255, b = data[o+2] / 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b);
    const v = max, s = max === 0 ? 0 : (max - min) / max;

    if (v < 0.05 || v > 0.95) continue;
    count++;
    totalS += s;
    if (s > 0.95) oversat++;
    if (s < 0.10) undersat++;
  }

  if (count === 0) {
    return { status: "no_data", recommended: currentSaturation, delta: 0 };
  }

  const meanS = totalS / count;
  const oversatPct = oversat / count;
  const undersatPct = undersat / count;
  const TARGET = 0.35;

  let diagnosis, recommended;

  if (oversatPct > 0.05) {
    const ratio = TARGET / Math.max(meanS, 0.01);
    recommended = Math.round(DEFAULT_1X * Math.min(ratio, 1.0));
    diagnosis = `Oversaturated: ${(oversatPct * 100).toFixed(1)}% at S>0.95. Reduce.`;
  } else if (undersatPct > 0.70 || meanS < 0.15) {
    const ratio = TARGET / Math.max(meanS, 0.01);
    recommended = Math.round(DEFAULT_1X * Math.max(ratio, 1.0));
    diagnosis = `Undersaturated: mean S=${meanS.toFixed(3)}. Increase.`;
  } else {
    const ratio = TARGET / meanS;
    recommended = Math.round(DEFAULT_1X * ratio);
    diagnosis = `Mean S=${meanS.toFixed(3)}. Fine-tuning toward ${TARGET}.`;
  }

  recommended = clampToRange(recommended, cap);

  return {
    status: oversatPct > 0.05
      ? "oversaturated" : undersatPct > 0.70
      ? "undersaturated" : "acceptable",
    recommended,
    delta: recommended - currentSaturation,
    reason: diagnosis,
    metrics: {
      meanS: +meanS.toFixed(3),
      oversatPct: +(oversatPct * 100).toFixed(1),
    },
  };
}
```

---

## 13. Gamma Analysis

Camera range: `[100, 500]`, step=1, default=300.
The encoding curve is `output = input^(1 / (setting/100))`.
Setting 300 = exponent 0.333, setting 220 = exponent 0.455 (sRGB-like).

### Algorithm

1. Compute luminance median.
2. Target median: ~118 (18% gray in sRGB space).
3. Calculate ideal gamma:
   `newGamma = currentGamma * ln(observedMedian/255) / ln(target/255)`
4. Clamp to [100, 500].

### Code

```javascript
function analyzeGamma(imageData, currentGamma, cap) {
  const lum = toLuminance(imageData);
  const hist = buildHistogram(lum);
  const n = lum.length;
  const median = histPercentile(hist, n, 0.5);
  const TARGET = 118;

  if (median <= 5) {
    return {
      recommended: cap.max, delta: cap.max - currentGamma,
      reason: "Extremely dark -- likely exposure issue. Max gamma as fallback.",
    };
  }
  if (median >= 250) {
    return {
      recommended: cap.min, delta: cap.min - currentGamma,
      reason: "Extremely bright -- likely exposure issue. Min gamma as fallback.",
    };
  }

  const currentG = currentGamma / 100;
  const newG = currentG * Math.log(median / 255) / Math.log(TARGET / 255);
  let recommended = Math.round(newG * 100);
  recommended = clampToRange(recommended, cap);

  const diff = Math.abs(median - TARGET);
  if (diff < 10 && Math.abs(recommended - currentGamma) < 15) {
    return {
      status: "optimal", recommended: currentGamma, delta: 0,
      reason: `Median ${median} is close to target ${TARGET}. No change needed.`,
      metrics: { median },
    };
  }

  return {
    status: median < TARGET ? "too_dark" : "too_bright",
    recommended,
    delta: recommended - currentGamma,
    reason: `Median ${median} -> target ${TARGET}. Gamma ${currentGamma} -> ${recommended}.`,
    metrics: { median },
  };
}
```

---

## 14. Hue Analysis

Camera range: `[-180, 180]`, step=1, default=0.
Rotates all colors uniformly in CbCr space. Rarely needs adjustment.

### Algorithm

1. Find near-neutral pixels (HSV saturation < 0.08, mid-brightness).
2. Compute their average R, G, B.
3. Convert the deviation from perfect gray into a CbCr angle.
4. Recommend counter-rotation scaled by cast magnitude.

### When to adjust

- Systematic camera color phase error (all colors rotated).
- Skin tones look wrong (too yellow-green or too magenta-red).
- A known reference color is shifted in a way WB alone cannot fix.

### Code

```javascript
function analyzeHue(imageData, currentHue, cap) {
  const { data, width, height } = imageData;
  const n = width * height;
  let nR = 0, nG = 0, nB = 0, nCount = 0;

  for (let i = 0; i < n; i++) {
    const o = i * 4;
    const r = data[o] / 255, g = data[o+1] / 255, b = data[o+2] / 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b);
    const v = max, s = max === 0 ? 0 : (max - min) / max;

    if (s < 0.08 && v > 0.15 && v < 0.85) {
      nR += data[o]; nG += data[o+1]; nB += data[o+2]; nCount++;
    }
  }

  if (nCount < 100) {
    return {
      status: "insufficient_data", recommended: currentHue, delta: 0,
      reason: "Too few neutral pixels. Leave hue at current value.",
    };
  }

  const avgR = nR / nCount, avgG = nG / nCount, avgB = nB / nCount;
  const avgLum = (avgR + avgG + avgB) / 3;
  const dR = avgR - avgLum, dG = avgG - avgLum, dB = avgB - avgLum;

  // CbCr deviation
  const cb = -0.169 * dR - 0.331 * dG + 0.500 * dB;
  const cr =  0.500 * dR - 0.419 * dG - 0.081 * dB;
  const mag = Math.sqrt(cb * cb + cr * cr);
  const angle = Math.atan2(cr, cb) * 180 / Math.PI;

  if (mag < 1.5) {
    return {
      status: "no_bias", recommended: currentHue, delta: 0,
      reason: `Negligible color cast (magnitude ${mag.toFixed(2)}). No hue adjustment needed.`,
      metrics: { castMagnitude: +mag.toFixed(2), castAngle: +angle.toFixed(1) },
    };
  }

  const confidence = Math.min(mag / 8, 1.0);
  let correction = Math.round(-angle * confidence);
  while (correction > 180) correction -= 360;
  while (correction < -180) correction += 360;

  const recommended = clampToRange(currentHue + correction, cap);

  return {
    status: mag < 3 ? "slight_bias" : "significant_bias",
    recommended,
    delta: recommended - currentHue,
    reason: `${mag < 3 ? "Slight" : "Significant"} color cast ` +
            `(mag=${mag.toFixed(2)}, angle=${angle.toFixed(0)} deg). ` +
            `Hue correction: ${correction}.`,
    metrics: { castMagnitude: +mag.toFixed(2), castAngle: +angle.toFixed(1) },
  };
}
```

---

## 15. Backlight Compensation Analysis

Camera range: `[0, 2]`, step=1, default=1.
0=off, 1=low, 2=high. Adjusts auto-exposure metering to favor the center
of the frame when a bright background is detected.

**Only effective when Exposure is in auto mode.** In manual exposure, BLC
has little to no effect on most UVC cameras.

### Algorithm

1. Divide image into center region (inner 50%) and edge region (outer 15%).
2. Compare average brightness: `diff = avgEdge - avgCenter`.
3. Check for bimodal histogram (two peaks: dark foreground + bright background).
4. Composite score: 60% edge-center diff, 20% dynamic range, 20% bimodality.
5. Map score to level: < 20 -> 0, 20-50 -> 1, > 50 -> 2.

### Code

```javascript
function analyzeBacklight(imageData, currentBLC, cap) {
  const { width, height } = imageData;
  const lum = toLuminance(imageData);
  const hist = buildHistogram(lum);
  const n = width * height;

  // Regions
  const cMargin = Math.floor(width * 0.25);
  const eMargin = Math.floor(width * 0.15);
  let cSum = 0, cCount = 0, eSum = 0, eCount = 0, totalSum = 0;

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const v = lum[y * width + x];
      totalSum += v;
      if (x >= cMargin && x < width - cMargin &&
          y >= cMargin && y < height - cMargin) {
        cSum += v; cCount++;
      }
      if (x < eMargin || x >= width - eMargin ||
          y < eMargin || y >= height - eMargin) {
        eSum += v; eCount++;
      }
    }
  }

  const avgCenter = cSum / cCount;
  const avgEdge = eSum / eCount;
  const avgTotal = totalSum / n;
  const diff = avgEdge - avgCenter;

  const p10 = histPercentile(hist, n, 0.10);
  const p90 = histPercentile(hist, n, 0.90);
  const dynamicRange = p90 - p10;

  // Bimodal detection (smoothed histogram)
  const smoothed = new Float64Array(256);
  for (let i = 0; i < 256; i++) {
    let s = 0, c = 0;
    for (let k = -5; k <= 5; k++) {
      const j = i + k;
      if (j >= 0 && j < 256) { s += hist[j]; c++; }
    }
    smoothed[i] = s / c;
  }

  let darkPeak = 0, darkVal = 0, brightPeak = 255, brightVal = 0;
  for (let i = 0; i <= 80; i++)
    if (smoothed[i] > darkVal) { darkVal = smoothed[i]; darkPeak = i; }
  for (let i = 175; i < 256; i++)
    if (smoothed[i] > brightVal) { brightVal = smoothed[i]; brightPeak = i; }

  let valleyMin = Infinity;
  for (let i = Math.min(darkPeak, brightPeak) + 10;
       i <= Math.max(darkPeak, brightPeak) - 10; i++) {
    if (smoothed[i] < valleyMin) valleyMin = smoothed[i];
  }
  const bimodalRatio = valleyMin > 0
    ? Math.min(darkVal, brightVal) / valleyMin : 0;
  const isBimodal = bimodalRatio > 2.5 &&
    darkVal > n * 0.002 && brightVal > n * 0.002;

  // Composite score (0-100)
  let score = 0;
  if (diff > 0) score += Math.min(diff / 80, 1) * 60;
  if (dynamicRange > 80 && avgCenter < 128)
    score += Math.min((dynamicRange - 80) / 100, 1) * 20;
  if (isBimodal) score += Math.min((bimodalRatio - 2.5) / 5, 1) * 20;
  if (avgCenter > 160) score *= 0.3;
  if (avgTotal < 30) score *= 0.5;

  const level = score < 20 ? 0 : score < 50 ? 1 : 2;

  return {
    recommended: clampToRange(level, cap),
    delta: level - currentBLC,
    reason: `Score ${score.toFixed(0)}/100. Center avg=${avgCenter.toFixed(0)}, ` +
            `edge avg=${avgEdge.toFixed(0)}, diff=${diff.toFixed(0)}.`,
    note: "Only effective when Exposure is in auto mode.",
    metrics: {
      score: +score.toFixed(0), avgCenter: +avgCenter.toFixed(0),
      avgEdge: +avgEdge.toFixed(0), dynamicRange, isBimodal,
    },
  };
}
```

---

## 16. Full Optimization Pipeline

Orchestrates all analyzers in the correct order with settling delays between
camera changes. Parallel groups share a single snapped ROI and apply their
changes concurrently.

VID property analysis uses **snapped ROI images** at the target capture
resolution because USB cameras often apply different ISP processing at
different resolutions. Focus uses the **video feed** for speed since
hill-climbing needs rapid iterations, but analyzes the same ROI region.

```javascript
/**
 * Run the full optimization pipeline on a camera ROI.
 *
 * @param {Function} snapROI       - Snaps a frame at capture resolution and
 *                                    returns a Promise<ImageData> of the
 *                                    ROI crop (size determined by frontend)
 * @param {Function} grabVideoROI  - Grabs a frame from the video feed and
 *                                    returns a Promise<ImageData> of the
 *                                    ROI crop (used for focus only)
 * @param {number}   settleMs      - Wait after each camera change (ms)
 * @param {Function} [onProgress]  - Called with { step, property, result }
 * @returns {Promise<Map>} Final capabilities state
 */
async function optimizeCamera(
  snapROI, grabVideoROI, settleMs = 300, onProgress
) {
  const caps = await getCapabilities();
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const report = (step, property, result) => {
    if (onProgress) onProgress({ step, property, result });
  };
  const hasCap = (key) => caps.has(key);
  const getCap = (key) => caps.get(key);

  async function apply(domain, property, recommended, current) {
    if (recommended !== current) {
      await setProperty(domain, property, recommended);
    }
  }

  let roi;

  // ── Step 1: Focus + Exposure (PARALLEL) ──────────────────────────
  // Focus: video feed ROI, hill-climbing (up to 30 iterations)
  // Exposure: snapped ROI (up to 5 snaps)
  // Independent CAM hardware controls — run concurrently.

  const step1Tasks = [];

  if (hasCap("cam.Focus")) {
    step1Tasks.push((async () => {
      const focusCap = getCap("cam.Focus");
      await setMode("cam", "Focus", "manual");
      await sleep(settleMs);

      const ctrl = createFocusController(focusCap.current, focusCap);
      for (let i = 0; i < 30; i++) {
        const f = await grabVideoROI();
        const r = ctrl.update(f);
        report(1, "Focus", r);
        if (r.status === "converged") break;
        if (r.status === "settling") { await sleep(100); continue; }
        await apply("cam", "Focus", r.recommendedFocus, focusCap.current);
        focusCap.current = r.recommendedFocus;
        await sleep(settleMs);
      }
    })());
  }

  if (hasCap("cam.Exposure")) {
    step1Tasks.push((async () => {
      const expCap = getCap("cam.Exposure");
      await setMode("cam", "Exposure", "manual");
      await sleep(settleMs);

      for (let i = 0; i < 5; i++) {
        const snap = await snapROI();
        const r = analyzeExposure(snap, expCap.current, expCap);
        report(1, "Exposure", r);
        if (r.stepsToApply === 0) break;
        await apply("cam", "Exposure", r.recommendedExposure, expCap.current);
        expCap.current = r.recommendedExposure;
        await sleep(settleMs);
      }
    })());
  }

  await Promise.all(step1Tasks);
  await sleep(settleMs);

  // ── Step 2: Gain + BacklightCompensation (PARALLEL) ──────────────
  // Analyze from the same snap, apply both concurrently.

  roi = await snapROI();
  const step2Applies = [];

  if (hasCap("vid.Gain") && hasCap("cam.Exposure")) {
    const gCap = getCap("vid.Gain");
    const eCap = getCap("cam.Exposure");
    const r = analyzeGain(roi, gCap.current, eCap.current, gCap, eCap);
    report(2, "Gain", r);
    if (r.delta !== 0) {
      step2Applies.push(apply("vid", "Gain", r.suggestedGain, gCap.current));
      gCap.current = r.suggestedGain;
    }
  }

  if (hasCap("vid.BacklightCompensation")) {
    const blcCap = getCap("vid.BacklightCompensation");
    const r = analyzeBacklight(roi, blcCap.current, blcCap);
    report(2, "BacklightCompensation", r);
    if (r.delta !== 0) {
      step2Applies.push(
        apply("vid", "BacklightCompensation", r.recommended, blcCap.current)
      );
    }
  }

  if (step2Applies.length > 0) {
    await Promise.all(step2Applies);
    await sleep(settleMs);
  }

  // ── Step 3: Gamma (SEQUENTIAL) ───────────────────────────────────

  if (hasCap("vid.Gamma")) {
    roi = await snapROI();
    const gCap = getCap("vid.Gamma");
    const r = analyzeGamma(roi, gCap.current, gCap);
    report(3, "Gamma", r);
    if (r.delta) {
      await apply("vid", "Gamma", r.recommended, gCap.current);
      await sleep(settleMs);
    }
  }

  // ── Step 4: Brightness (SEQUENTIAL) ──────────────────────────────

  if (hasCap("vid.Brightness")) {
    roi = await snapROI();
    const bCap = getCap("vid.Brightness");
    const r = analyzeBrightness(roi, bCap.current, bCap);
    report(4, "Brightness", r);
    if (r.delta) {
      await apply("vid", "Brightness", r.recommended, bCap.current);
      await sleep(settleMs);
    }
  }

  // ── Step 5: Contrast (SEQUENTIAL) ────────────────────────────────

  if (hasCap("vid.Contrast")) {
    roi = await snapROI();
    const cCap = getCap("vid.Contrast");
    const r = analyzeContrast(roi, cCap.current, cCap);
    report(5, "Contrast", r);
    if (r.delta) {
      await apply("vid", "Contrast", r.recommended, cCap.current);
      await sleep(settleMs);
    }
  }

  // ── Step 6: WhiteBalance (SEQUENTIAL, iterative) ─────────────────

  if (hasCap("vid.WhiteBalance")) {
    const wbCap = getCap("vid.WhiteBalance");
    await setMode("vid", "WhiteBalance", "manual");
    await sleep(settleMs);

    for (let i = 0; i < 3; i++) {
      roi = await snapROI();
      const r = analyzeWhiteBalance(roi, wbCap.current, wbCap);
      report(6, "WhiteBalance", r);
      if (r.preferAuto) {
        await setMode("vid", "WhiteBalance", "auto");
        break;
      }
      if (Math.abs(r.delta) < wbCap.step * 2) break;
      await apply("vid", "WhiteBalance", r.recommended, wbCap.current);
      wbCap.current = r.recommended;
      await sleep(settleMs);
    }
  }

  // ── Step 7: Saturation + Hue (PARALLEL) ──────────────────────────
  // Independent color axes: magnitude vs angle.

  roi = await snapROI();
  const step7Applies = [];

  if (hasCap("vid.Saturation")) {
    const sCap = getCap("vid.Saturation");
    const r = analyzeSaturation(roi, sCap.current, sCap);
    report(7, "Saturation", r);
    if (r.delta) {
      step7Applies.push(
        apply("vid", "Saturation", r.recommended, sCap.current)
      );
    }
  }

  if (hasCap("vid.Hue")) {
    const hCap = getCap("vid.Hue");
    const r = analyzeHue(roi, hCap.current, hCap);
    report(7, "Hue", r);
    if (r.delta) {
      step7Applies.push(apply("vid", "Hue", r.recommended, hCap.current));
    }
  }

  if (step7Applies.length > 0) {
    await Promise.all(step7Applies);
    await sleep(settleMs);
  }

  // ── Step 8: Sharpness (SEQUENTIAL, must be last) ─────────────────

  if (hasCap("vid.Sharpness")) {
    roi = await snapROI();
    const shCap = getCap("vid.Sharpness");
    const r = analyzeSharpness(roi, shCap.current, shCap);
    report(8, "Sharpness", r);
    if (r.delta) {
      await apply("vid", "Sharpness", r.recommended, shCap.current);
    }
  }

  return getCapabilities();
}
```

### Usage

```javascript
/**
 * Snap function: captures a frame at the target resolution,
 * crops the ROI, and returns an ImageData of the ROI.
 * Used for VID property analysis — must use the actual capture
 * resolution because the camera ISP applies different processing
 * per resolution. ROI size is determined by the frontend.
 */
async function snapROI() {
  const fullImage = await captureAtTargetResolution();
  const canvas = document.createElement("canvas");
  canvas.width = roiW;
  canvas.height = roiH;
  const ctx = canvas.getContext("2d");
  ctx.drawImage(fullImage, roiX, roiY, roiW, roiH, 0, 0, roiW, roiH);
  return ctx.getImageData(0, 0, roiW, roiH);
}

/**
 * Video feed grab: crops the ROI from the live video element.
 * Used for Focus hill-climbing — needs rapid iterations so uses
 * the video feed instead of snapping. Same ROI region and size.
 */
function grabVideoROI() {
  const canvas = document.createElement("canvas");
  canvas.width = roiW;
  canvas.height = roiH;
  const ctx = canvas.getContext("2d");
  ctx.drawImage(videoEl, roiX, roiY, roiW, roiH, 0, 0, roiW, roiH);
  return Promise.resolve(ctx.getImageData(0, 0, roiW, roiH));
}

// Run full optimization
optimizeCamera(snapROI, grabVideoROI, 300, ({ step, property, result }) => {
  console.log(`[Step ${step}] ${property}:`, result.reason);
}).then((finalCaps) => {
  console.log("Optimization complete", finalCaps);
});
```
