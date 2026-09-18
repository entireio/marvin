/* ==========================================================================
   Marvin — site behaviour

   The design artboards were prototyped in Claude Design, where the interactive
   bits were React bindings resolved by a canvas runtime. That runtime is not
   shipped, so this file reimplements the video and assembly behaviours in plain
   JavaScript against the data-attribute hooks left behind by the conversion:

     data-action="togglePlay"    the hero video
     data-action="toggleDemo"    the demo video
     data-ref="anatomyRef"       the exploded-view assembly
     data-ref="readoutRef"       its state readout

   No dependencies, no build step — matching how the controller app is built.
   ========================================================================== */

(function () {
  'use strict';

  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  var pageScroller = null;

  function wireSafariScrollport() {
    // Desktop Safari clips page decorations at the root rubber-band boundary.
    // Keep native overflow scrolling, but put the decorative frame outside it.
    if (navigator.vendor !== 'Apple Computer, Inc.' ||
        !/Macintosh/.test(navigator.userAgent) ||
        /CriOS|FxiOS|Edg|Chrome/.test(navigator.userAgent)) return;
    var previousScroll = window.scrollY;
    pageScroller = document.createElement('div');
    pageScroller.className = 'safari-page-scrollport';
    pageScroller.tabIndex = 0;
    pageScroller.setAttribute('role', 'region');
    pageScroller.setAttribute('aria-label', 'Page content');
    while (document.body.firstChild) pageScroller.appendChild(document.body.firstChild);
    document.body.appendChild(pageScroller);
    document.documentElement.classList.add('safari-framed-scroll');
    pageScroller.scrollTop = previousScroll;
    // Preserve direct links when the initial fragment was resolved before wrapping.
    if (location.hash) {
      var fragment = location.hash.slice(1);
      try { fragment = decodeURIComponent(fragment); } catch (_) { /* Keep literal malformed fragments. */ }
      var target = document.getElementById(fragment);
      if (target) target.scrollIntoView();
    }
  }

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

    (pageScroller || window).addEventListener('scroll', onScroll, { passive: true });
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

  // Native disclosure menus retain working navigation without JavaScript.
  function wireNavigation() {
    var headerMenu = document.querySelector('.site-navigation');
    var docsMenu = document.querySelector('.docs-menu');
    var mobileHeader = window.matchMedia('(max-width: 767px)');
    var mobileDocs = window.matchMedia('(max-width: 900px)');
    document.documentElement.classList.add('has-site-js');
    var savedOverflow = null;
    var pageContent = document.querySelector('main');
    var footer = document.querySelector('.site-footer');
    function paintHeaderMenu() {
      if (!headerMenu) return;
      var open = mobileHeader.matches && headerMenu.open;
      var trigger = headerMenu.querySelector('summary');
      trigger.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
      trigger.querySelector('path').setAttribute('d', open ? 'M6 6l12 12M6 18L18 6' : 'M4 7h16M4 12h16M4 17h16');
      if (pageContent) pageContent.inert = open;
      if (footer) footer.inert = open;
      if (open && savedOverflow === null) {
        savedOverflow = document.body.style.overflow;
        document.body.style.overflow = 'hidden';
      } else if (!open && savedOverflow !== null) {
        document.body.style.overflow = savedOverflow;
        savedOverflow = null;
      }
    }

    function syncHeader() { if (headerMenu) headerMenu.open = !mobileHeader.matches; paintHeaderMenu(); }
    function syncDocs() { if (docsMenu) docsMenu.open = !mobileDocs.matches; }
    syncHeader();
    syncDocs();
    addChangeListener(mobileHeader, syncHeader);
    addChangeListener(mobileDocs, syncDocs);

    function closeMenu(menu, restoreFocus) {
      if (!menu || !menu.open) return;
      menu.open = false;
      if (restoreFocus) menu.querySelector('summary').focus();
    }
    document.addEventListener('keydown', function (event) {
      if (event.key === 'Tab' && mobileHeader.matches && headerMenu && headerMenu.open) {
        var focusable = [].slice.call(document.querySelectorAll('.site-header a, .site-header summary'));
        var first = focusable[0];
        var last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
      }
      if (event.key !== 'Escape') return;
      if (mobileHeader.matches && headerMenu && headerMenu.open) closeMenu(headerMenu, true);
      else if (mobileDocs.matches) closeMenu(docsMenu, true);
    });
    document.addEventListener('click', function (event) {
      if (mobileHeader.matches && headerMenu && !headerMenu.contains(event.target)) closeMenu(headerMenu, false);
    });
    each('.docs-sidebar a[href^="#"]', function (link) {
      link.addEventListener('click', function () {
        if (mobileDocs.matches) closeMenu(docsMenu, false);
      });
    });
    if (headerMenu) headerMenu.addEventListener('toggle', function () {
      paintHeaderMenu();
      if (mobileHeader.matches && headerMenu.open && mobileDocs.matches) closeMenu(docsMenu, false);
    });
    if (docsMenu) docsMenu.addEventListener('toggle', function () {
      if (mobileDocs.matches && docsMenu.open && mobileHeader.matches) closeMenu(headerMenu, false);
    });
  }

  function wireScrollRegions() {
    each('.table-scroll, .diagram-scroll', function (region) {
      function update() {
        var overflowing = region.scrollWidth > region.clientWidth + 1;
        region.classList.toggle('is-scrollable', overflowing);
        region.tabIndex = overflowing ? 0 : -1;
      }
      update();
      if (window.ResizeObserver) new ResizeObserver(update).observe(region);
      else window.addEventListener('resize', update);
      if (document.fonts) document.fonts.ready.then(update);
    });
  }

  // Track the section at the reading edge, below the sticky navigation.
  // Scrolling updates aria-current without changing the URL or browser history.
  function wireSectionNavigation() {
    var nav = document.querySelector('.docs-sidebar__subnav') ||
      document.querySelector('.contribute-page nav[aria-label="On this page"]');
    if (!nav) return;
    var entries = [];
    each( '.docs-sidebar__subnav a[href^="#"], .contribute-page nav[aria-label="On this page"] a[href^="#"]', function (link) {
      var section = document.getElementById(decodeURIComponent(link.hash.slice(1)));
      if (section) entries.push({ link: link, section: section });
    });
    if (!entries.length) return;
    var active = null;
    var queued = false;
    var docsMenu = document.querySelector('.docs-menu');
    var mobileDocs = window.matchMedia('(max-width: 900px)');
    var horizontalRail = nav.querySelector('ol');

    function select(entry) {
      if (entry === active) return;
      active = entry;
      entries.forEach(function (item) {
        if (item === entry) item.link.setAttribute('aria-current', 'location');
        else item.link.removeAttribute('aria-current');
      });
      // Reveal the selected secondary-nav item without scrolling the page.
      if (entry && horizontalRail) {
        var rail = horizontalRail.getBoundingClientRect();
        var link = entry.link.getBoundingClientRect();
        if (link.left < rail.left + 16) horizontalRail.scrollLeft += link.left - rail.left - 16;
        else if (link.right > rail.right - 16) horizontalRail.scrollLeft += link.right - rail.right + 16;
      }
    }

    function update() {
      queued = false;
      // Expanding the mobile contents menu temporarily shifts the article.
      if (docsMenu && mobileDocs.matches && docsMenu.open) return;
      var readingEdge = parseFloat(getComputedStyle(entries[0].section).scrollMarginTop) || 96;
      var current = null;
      entries.forEach(function (entry) {
        if (entry.section.getBoundingClientRect().top <= readingEdge + 1) current = entry;
      });
      var scrollTop = pageScroller ? pageScroller.scrollTop : window.scrollY;
      var scrollHeight = pageScroller ? pageScroller.scrollHeight : document.documentElement.scrollHeight;
      if (scrollTop > 0 && scrollTop + window.innerHeight >= scrollHeight - 2) {
        current = entries[entries.length - 1];
      }
      select(current);
    }
    function schedule() {
      if (!queued) { queued = true; window.requestAnimationFrame(update); }
    }
    entries.forEach(function (entry) {
      entry.link.addEventListener('click', function (event) {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        select(entry);
        schedule();
      });
    });
    (pageScroller || window).addEventListener('scroll', schedule, { passive: true });
    window.addEventListener('resize', schedule);
    window.addEventListener('hashchange', schedule);
    window.addEventListener('pageshow', schedule);
    window.addEventListener('load', schedule);
    if (docsMenu) docsMenu.addEventListener('toggle', schedule);
    if (window.ResizeObserver) new ResizeObserver(schedule).observe(document.querySelector('main'));
    if (document.fonts) document.fonts.ready.then(schedule);
    schedule();
  }

  // Ornament must adapt to spare space, never ask the layout to make room.
  function wireMarginStudies() {
    const hosts = [...document.querySelectorAll('.study-host')];
    if (!hosts.length || !window.ResizeObserver) return;
    const measure = () => hosts.forEach(host => {
      const study = host.querySelector('.margin-study');
      const content = study.previousElementSibling;
      const spare = host.getBoundingClientRect().bottom - content.getBoundingClientRect().bottom - 28;
      study.style.setProperty('--study-space', `${Math.max(0, spare)}px`);
      study.toggleAttribute('data-fits', spare >= 135);
    });
    const observer = new ResizeObserver(measure);
    hosts.forEach(host => {
      observer.observe(host);
      observer.observe(host.querySelector('.margin-study').previousElementSibling);
    });
    if (document.fonts) document.fonts.ready.then(measure);
    measure();
  }

  function init() {
    wireSafariScrollport();
    wireNavigation();
    wireScrollRegions();
    wireSectionNavigation();
    silenceVideos();
    wireVideo('videoRef', 'togglePlay');
    wireVideo('demoRef', 'toggleDemo');
    wireAnatomy();
    wireMarginStudies();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
