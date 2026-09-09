/* ==========================================================================
   Marvin — site behaviour

   The design artboards were prototyped in Claude Design, where the interactive
   bits were React bindings resolved by a canvas runtime. That runtime is not
   shipped, so this file reimplements the same four behaviours in plain
   JavaScript against the data-attribute hooks left behind by the conversion:

     data-action="toggleTheme"   the colour-scheme switch
     data-action="togglePlay"    the hero video
     data-action="toggleDemo"    the demo video
     data-ref="anatomyRef"       the exploded-view assembly
     data-ref="readoutRef"       its state readout

   No dependencies, no build step — matching how the controller app is built.
   ========================================================================== */

(function () {
  'use strict';

  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  var darkScheme = window.matchMedia('(prefers-color-scheme: dark)');

  /* ------------------------------------------------------------------ */
  /* Colour scheme                                                       */
  /* ------------------------------------------------------------------ */
  // The stylesheet handles the system preference on its own. We only write
  // data-theme on <html> when the visitor has made an explicit choice, so the
  // untouched default keeps following the OS.

  var STORAGE_KEY = 'marvin-theme';

  function storedTheme() {
    try {
      var v = localStorage.getItem(STORAGE_KEY);
      return v === 'light' || v === 'dark' ? v : null;
    } catch (e) {
      return null; // private mode, or site data blocked
    }
  }

  function effectiveTheme() {
    return storedTheme() || (darkScheme.matches ? 'dark' : 'light');
  }

  function paintThemeButtons() {
    // The button offers the scheme you would switch *to*.
    var next = effectiveTheme() === 'dark' ? 'Light' : 'Dark';
    each('[data-action="toggleTheme"]', function (btn) {
      btn.textContent = next;
      btn.setAttribute('aria-label', 'Switch to ' + next.toLowerCase() + ' colour scheme');
    });
  }

  function applyStoredTheme() {
    var stored = storedTheme();
    if (stored) {
      document.documentElement.setAttribute('data-theme', stored);
    } else {
      document.documentElement.removeAttribute('data-theme');
    }
  }

  function toggleTheme() {
    var next = effectiveTheme() === 'dark' ? 'light' : 'dark';
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch (e) {
      /* not fatal — the choice just will not persist */
    }
    document.documentElement.setAttribute('data-theme', next);
    paintThemeButtons();
  }

  // Keep the label honest if the OS flips while no explicit choice is stored.
  addChangeListener(darkScheme, function () {
    if (!storedTheme()) paintThemeButtons();
  });

  /* ------------------------------------------------------------------ */
  /* Videos                                                              */
  /* ------------------------------------------------------------------ */
  // Every clip here is wallpaper, not a talk — none of them has anything to
  // say, and a page that starts making noise on its own is a page people close.
  // The files ship with no audio track at all and the markup carries `muted`;
  // this is the third lock, for the cases the markup cannot cover — a restore
  // from the back/forward cache, a browser media control, an extension.

  function silence(video) {
    video.defaultMuted = true;
    video.muted = true;
    video.volume = 0;
  }

  function silenceVideos() {
    each('video', function (video) {
      silence(video);
      video.addEventListener('volumechange', function () {
        // Guarded, or re-muting would fire this handler forever.
        if (!video.muted || video.volume !== 0) silence(video);
      });
    });
  }

  function wireVideo(refName, actionName) {
    var video = document.querySelector('[data-ref="' + refName + '"]');
    var button = document.querySelector('[data-action="' + actionName + '"]');
    if (!video || !button) return;

    function paint() {
      var paused = video.paused;
      button.textContent = paused ? 'Play' : 'Pause';
      button.setAttribute(
        'aria-label',
        (paused ? 'Play' : 'Pause') + ' the video in this figure'
      );
    }

    button.addEventListener('click', function () {
      if (video.paused) {
        var p = video.play();
        if (p && p.catch) p.catch(function () {});
      } else {
        video.pause();
      }
    });

    video.addEventListener('play', paint);
    video.addEventListener('pause', paint);

    // Autoplaying motion is the sort of thing prefers-reduced-motion exists to
    // stop. Hold on the poster frame instead and let the visitor start it.
    if (reduceMotion.matches) {
      video.removeAttribute('autoplay');
      video.pause();
    }

    paint();
  }

  /* ------------------------------------------------------------------ */
  /* Exploded view                                                       */
  /* ------------------------------------------------------------------ */
  // Each part group carries data-dx / data-dy: its offset in the exploded
  // state. Scrolling the figure up the viewport interpolates those offsets to
  // zero, so the robot assembles itself as you read past it.

  function wireAnatomy() {
    var scope = document.querySelector('[data-ref="anatomyRef"]');
    if (!scope) return;

    var figure = scope.querySelector('svg') || scope;
    var parts = [].slice.call(scope.querySelectorAll('[data-dx]'));
    var readout = document.querySelector('[data-ref="readoutRef"]');
    if (!parts.length) return;

    function draw(explosion) {
      parts.forEach(function (part) {
        var dx = parseFloat(part.getAttribute('data-dx')) || 0;
        var dy = parseFloat(part.getAttribute('data-dy')) || 0;
        part.style.transform =
          'translate(' + dx * explosion + 'px,' + dy * explosion + 'px)';
      });
      if (readout) {
        readout.textContent =
          explosion > 0.6 ? 'Exploded'
            : explosion > 0.04 ? 'Assembling'
              : 'Assembled';
      }
    }

    // With reduced motion the drawing simply stays exploded, which is the more
    // legible of the two states anyway.
    if (reduceMotion.matches) {
      draw(1);
      return;
    }

    var queued = false;

    function update() {
      queued = false;
      var box = figure.getBoundingClientRect();
      var vh = window.innerHeight || document.documentElement.clientHeight;
      // 0 as the figure enters from below, 1 once it has risen through 75% of
      // the viewport. Assembly progress; explosion is its complement.
      var progress = (vh - box.top) / (vh * 0.75);
      progress = progress < 0 ? 0 : progress > 1 ? 1 : progress;
      draw(1 - progress);
    }

    function onScroll() {
      if (queued) return;
      queued = true;
      window.requestAnimationFrame(update);
    }

    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onScroll);
    // requestAnimationFrame is frozen while the tab is hidden, so the drawing
    // can be left mid-assembly from whenever it was last painted. Re-sync it
    // when the page comes back rather than waiting for the next scroll.
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) update();
    });
    addChangeListener(reduceMotion, function () {
      if (reduceMotion.matches) draw(1);
    });
    update();
  }

  /* ------------------------------------------------------------------ */
  /* Helpers                                                             */
  /* ------------------------------------------------------------------ */

  function each(selector, fn) {
    [].slice.call(document.querySelectorAll(selector)).forEach(fn);
  }

  function addChangeListener(mql, fn) {
    if (mql.addEventListener) mql.addEventListener('change', fn);
    else if (mql.addListener) mql.addListener(fn); // Safari < 14
  }

  /* ------------------------------------------------------------------ */

  function init() {
    applyStoredTheme();
    paintThemeButtons();
    each('[data-action="toggleTheme"]', function (btn) {
      btn.addEventListener('click', toggleTheme);
    });
    silenceVideos();
    wireVideo('videoRef', 'togglePlay');
    wireVideo('demoRef', 'toggleDemo');
    wireAnatomy();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
