namespace ContentPilot.Renderer.Engine;

/// <summary>
/// Browser-side scripts. Measurement happens here rather than in pixel analysis because
/// the layout engine already knows every answer exactly — whether a headline overflows is
/// a property of the box, not something to infer from an image.
/// </summary>
public static class RenderScripts
{
    /// <summary>
    /// Bounded font-size reduction for slots that opted in. It absorbs near-miss overflow
    /// without spending a retry, and reports when it fired so repeated use shows up as a
    /// budget that is wrong rather than as silently shrinking type.
    /// </summary>
    public const string ShrinkToFit =
        """
        (config) => {
          const applied = [];

          for (const slot of config.slots) {
            const el = document.querySelector(`[data-slot="${slot.id}"]`);
            if (!el || !slot.shrinkToFit) continue;

            const start = parseFloat(getComputedStyle(el).fontSize);
            let size = start;
            const floor = Math.max(slot.minFontPx || 0, 1);

            const overflowing = () =>
              el.scrollHeight > el.clientHeight + 1 || el.scrollWidth > el.clientWidth + 1;

            while (overflowing() && size > floor) {
              size = Math.max(floor, size - 1);
              el.style.fontSize = size + 'px';
            }

            if (size < start) {
              el.dataset.shrunk = 'true';
              applied.push({ id: slot.id, from: start, to: size });
            }
          }

          return applied;
        }
        """;

    /// <summary>
    /// Reads out what the browser measured. Coordinates are multiplied by the device scale
    /// factor so every box in the report is in the same pixel space as the screenshot.
    /// </summary>
    public const string CollectReport =
        """
        (dsf) => {
          // Any non-finite number here would fail JSON serialisation and take the whole
          // render down. A measurement pipeline must never be able to do that, so every
          // value is coerced on the way out.
          const num = (v) => (typeof v === 'number' && isFinite(v)) ? v : 0;

          const frame = document.getElementById('frame');
          const frameBox = frame.getBoundingClientRect();

          const styles = getComputedStyle(document.documentElement);
          const pct = (name) => parseFloat(styles.getPropertyValue(name)) / 100;
          const safe = {
            top: pct('--safe-top') * frameBox.height,
            bottom: pct('--safe-bottom') * frameBox.height,
            x: pct('--safe-x') * frameBox.width,
          };

          const toBox = (r) => ({
            x: num((r.left - frameBox.left) * dsf),
            y: num((r.top - frameBox.top) * dsf),
            width: num(r.width * dsf),
            height: num(r.height * dsf),
          });

          const breaksSafeArea = (r) =>
            (r.top - frameBox.top) < safe.top ||
            (frameBox.bottom - r.bottom) < safe.bottom ||
            (r.left - frameBox.left) < safe.x ||
            (frameBox.right - r.right) < safe.x;

          const lineCount = (el) => {
            const lh = parseFloat(getComputedStyle(el).lineHeight);
            if (!isFinite(lh) || lh <= 0) return 1;
            return num(Math.max(1, Math.round(el.getBoundingClientRect().height / lh)));
          };

          const slots = [];

          for (const el of document.querySelectorAll('[data-slot]')) {
            const r = el.getBoundingClientRect();
            const cs = getComputedStyle(el);

            slots.push({
              slotId: el.dataset.slot,
              kind: 'text',
              box: toBox(r),
              // The layout engine's own answer. No false positives, unlike reading pixels.
              overflows: el.scrollHeight > el.clientHeight + 1 || el.scrollWidth > el.clientWidth + 1,
              lineCount: lineCount(el),
              fontSizePx: num(parseFloat(cs.fontSize) * dsf),
              shrinkApplied: el.dataset.shrunk === 'true',
              foregroundColor: cs.color,
              breaksSafeArea: breaksSafeArea(r),
              empty: el.textContent.trim().length === 0,
            });
          }

          for (const el of document.querySelectorAll('[data-asset-slot]')) {
            const r = el.getBoundingClientRect();
            const img = el.querySelector('img');

            slots.push({
              slotId: el.dataset.assetSlot,
              kind: 'asset',
              box: toBox(r),
              overflows: false,
              lineCount: 0,
              fontSizePx: 0,
              shrinkApplied: false,
              foregroundColor: null,
              breaksSafeArea: breaksSafeArea(r),
              empty: !img || !img.getAttribute('src'),
              naturalWidth: img ? img.naturalWidth : 0,
              naturalHeight: img ? img.naturalHeight : 0,
            });
          }

          return {
            width: num(Math.round(frameBox.width * dsf)),
            height: num(Math.round(frameBox.height * dsf)),
            slots,
            fontsLoaded: [...new Set([...document.fonts].filter(f => f.status === 'loaded').map(f => f.family))],
          };
        }
        """;

    /// <summary>
    /// Paints the page so one asset slot is pure white and everything else is pure black.
    /// The slot's exact device-pixel box and the fraction of it covered by later-painted
    /// elements then fall straight out of the image — no heuristics, no thresholds.
    /// </summary>
    public const string ApplyMask =
        """
        (slotId) => {
          const previous = document.getElementById('cp-mask-style');
          if (previous) previous.remove();

          const el = document.querySelector(`[data-asset-slot="${slotId}"]`);
          if (!el) return null;

          // Only elements that actually paint may darken the slot. Blackening every
          // element would turn ordinary transparent layout containers into occluders and
          // report a perfectly clean render as heavily covered — a false positive that
          // would send the item round the remediation loop for nothing.
          const paints = (node) => {
            const cs = getComputedStyle(node);

            if (cs.visibility === 'hidden' || cs.display === 'none' || parseFloat(cs.opacity) === 0) {
              return false;
            }

            const bg = cs.backgroundColor;
            const transparent = !bg || bg === 'transparent' || /rgba\(\s*0,\s*0,\s*0,\s*0\s*\)/.test(bg);

            if (!transparent) return true;
            if (cs.backgroundImage && cs.backgroundImage !== 'none') return true;
            if (parseFloat(cs.borderTopWidth) > 0 || parseFloat(cs.borderBottomWidth) > 0 ||
                parseFloat(cs.borderLeftWidth) > 0 || parseFloat(cs.borderRightWidth) > 0) return true;

            // Text painted directly by this element, not by a descendant.
            for (const child of node.childNodes) {
              if (child.nodeType === Node.TEXT_NODE && child.textContent.trim().length > 0) return true;
            }

            return false;
          };

          const marked = [];

          for (const node of document.querySelectorAll('#frame *')) {
            if (node === el || el.contains(node) || node.contains(el)) continue;
            if (!paints(node)) continue;

            node.classList.add('cp-occluder');
            marked.push(node);
          }

          const style = document.createElement('style');
          style.id = 'cp-mask-style';
          style.textContent = `
            html, body, #frame {
              background: #000 !important;
              background-image: none !important;
            }
            #frame .cp-occluder {
              background-color: #000 !important;
              background-image: none !important;
              color: #000 !important;
              border-color: #000 !important;
              box-shadow: none !important;
              text-shadow: none !important;
              filter: none !important;
              opacity: 1 !important;
            }
            #frame img, #frame svg, #frame video, #frame canvas { visibility: hidden !important; }
            /* Must outrank the occluder rule: an ancestor marked as painting would
               otherwise darken the slot sitting inside it. */
            #frame [data-asset-slot="${slotId}"],
            #frame [data-asset-slot="${slotId}"] * {
              background-color: #FFF !important;
              background-image: none !important;
              color: #FFF !important;
              border-color: #FFF !important;
              box-shadow: none !important;
              filter: none !important;
              opacity: 1 !important;
            }
          `;

          document.head.appendChild(style);
          document.body.dataset.cpMarked = marked.length;

          const frameBox = document.getElementById('frame').getBoundingClientRect();
          const r = el.getBoundingClientRect();

          return {
            x: r.left - frameBox.left,
            y: r.top - frameBox.top,
            width: r.width,
            height: r.height,
          };
        }
        """;

    public const string RemoveMask =
        """
        () => {
          const style = document.getElementById('cp-mask-style');
          if (style) style.remove();

          for (const node of document.querySelectorAll('.cp-occluder')) {
            node.classList.remove('cp-occluder');
          }

          delete document.body.dataset.cpMarked;
        }
        """;

    /// <summary>
    /// Fonts must be ready and every image decoded before the screenshot, or the render is
    /// a race. Both are awaited explicitly rather than trusted to load ordering.
    /// </summary>
    public const string WaitForPaint =
        """
        async () => {
          await document.fonts.ready;
          const images = [...document.images];
          await Promise.all(images.map(i => (i.decode ? i.decode().catch(() => {}) : Promise.resolve())));
          return images.every(i => i.complete && i.naturalWidth > 0);
        }
        """;
}
