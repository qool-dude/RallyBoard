(function () {
  function fit(el, minPx) {
    if (!el) return;
    minPx = minPx || 10;
    el.style.fontSize = "";
    var maxPx = parseFloat(getComputedStyle(el).fontSize) || 16;
    el.style.fontSize = maxPx + "px";
    if (el.scrollWidth <= el.clientWidth + 0.5) return;

    var lo = minPx;
    var hi = maxPx;
    while (hi - lo > 0.25) {
      var mid = (lo + hi) / 2;
      el.style.fontSize = mid + "px";
      if (el.scrollWidth <= el.clientWidth + 0.5) lo = mid;
      else hi = mid;
    }
    el.style.fontSize = lo + "px";
  }

  window.rallyFitChipName = function (el, minPx) {
    fit(el, minPx);
  };

  window.rallyObserveChipName = function (el, minPx) {
    if (!el) return;
    if (el._rallyFitObs) {
      fit(el, minPx);
      return;
    }
    var run = function () { fit(el, minPx); };
    var ro = new ResizeObserver(run);
    ro.observe(el);
    el._rallyFitObs = ro;
    run();
  };

  window.rallyUnobserveChipName = function (el) {
    if (!el || !el._rallyFitObs) return;
    el._rallyFitObs.disconnect();
    delete el._rallyFitObs;
    el.style.fontSize = "";
  };
})();
