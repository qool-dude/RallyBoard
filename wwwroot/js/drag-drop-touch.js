/**
 * Touch → HTML5 Drag & Drop bridge for tablets/phones.
 * Native HTML5 DnD is desktop-only; long-press otherwise opens share/print menus.
 */
(function () {
  'use strict';

  if (window.__rallyBoardDragDropTouch) return;
  window.__rallyBoardDragDropTouch = true;

  var THRESHOLD = 8;
  var HOLD_MS = 180;

  var dragSrc = null;
  var avatar = null;
  var lastTarget = null;
  var startX = 0;
  var startY = 0;
  var active = false;
  var holdTimer = null;
  var dataTransfer = null;
  var pressTouchId = null;

  function isInteractive(el) {
    return !!(el && el.closest('button, a, input, select, textarea, label, .chip__menu, .chip-menu-backdrop'));
  }

  function closestDraggable(el) {
    while (el && el !== document.body) {
      if (el.getAttribute && el.getAttribute('draggable') === 'true') return el;
      el = el.parentElement;
    }
    return null;
  }

  function createDataTransfer() {
    var store = {};
    return {
      dropEffect: 'move',
      effectAllowed: 'all',
      files: [],
      types: [],
      setData: function (type, val) {
        store[type] = String(val);
        if (this.types.indexOf(type) < 0) this.types.push(type);
      },
      getData: function (type) { return store[type] || ''; },
      clearData: function (type) {
        if (type) {
          delete store[type];
          this.types = this.types.filter(function (t) { return t !== type; });
        } else {
          store = {};
          this.types = [];
        }
      },
      setDragImage: function () { }
    };
  }

  function dispatch(target, type, touch, extra) {
    if (!target) return true;
    var evt = new Event(type, { bubbles: true, cancelable: true });
    evt.dataTransfer = dataTransfer;
    evt.button = 0;
    evt.which = 1;
    evt.buttons = type === 'dragend' || type === 'drop' ? 0 : 1;
    if (touch) {
      evt.clientX = touch.clientX;
      evt.clientY = touch.clientY;
      evt.pageX = touch.pageX;
      evt.pageY = touch.pageY;
      evt.screenX = touch.screenX;
      evt.screenY = touch.screenY;
    }
    if (extra) Object.assign(evt, extra);
    return target.dispatchEvent(evt);
  }

  function clearHold() {
    if (holdTimer) {
      clearTimeout(holdTimer);
      holdTimer = null;
    }
  }

  function destroyAvatar() {
    if (avatar && avatar.parentNode) avatar.parentNode.removeChild(avatar);
    avatar = null;
  }

  function moveAvatar(touch) {
    if (!avatar) return;
    avatar.style.transform =
      'translate(' + (touch.clientX - 40) + 'px,' + (touch.clientY - 20) + 'px)';
  }

  function elementFromTouch(touch) {
    if (avatar) avatar.style.display = 'none';
    var el = document.elementFromPoint(touch.clientX, touch.clientY);
    if (avatar) avatar.style.display = '';
    return el;
  }

  function endDrag(touch, cancelled) {
    clearHold();
    if (!active) {
      dragSrc = null;
      pressTouchId = null;
      return;
    }

    var target = lastTarget;
    if (!cancelled && target) {
      dispatch(target, 'dragover', touch);
      dispatch(target, 'drop', touch);
    }
    if (lastTarget && lastTarget !== dragSrc) {
      dispatch(lastTarget, 'dragleave', touch);
    }
    dispatch(dragSrc, 'dragend', touch);

    if (dragSrc) dragSrc.classList.remove('chip--dragging');
    destroyAvatar();
    active = false;
    dragSrc = null;
    lastTarget = null;
    dataTransfer = null;
    pressTouchId = null;
  }

  function beginDrag(touch) {
    if (!dragSrc || active) return;
    active = true;
    dataTransfer = createDataTransfer();
    dataTransfer.setData('text', 'player');
    dataTransfer.setData('text/plain', 'player');

    dragSrc.classList.add('chip--dragging');
    dispatch(dragSrc, 'dragstart', touch);

    avatar = dragSrc.cloneNode(true);
    avatar.classList.add('chip--drag-avatar');
    avatar.style.position = 'fixed';
    avatar.style.left = '0';
    avatar.style.top = '0';
    avatar.style.zIndex = '10000';
    avatar.style.pointerEvents = 'none';
    avatar.style.opacity = '0.92';
    avatar.style.width = Math.min(dragSrc.offsetWidth, 220) + 'px';
    avatar.style.margin = '0';
    document.body.appendChild(avatar);
    moveAvatar(touch);
  }

  function onTouchStart(e) {
    if (e.touches.length !== 1) return;
    var touch = e.touches[0];
    var target = e.target;
    if (isInteractive(target)) return;

    var src = closestDraggable(target);
    if (!src) return;

    dragSrc = src;
    pressTouchId = touch.identifier;
    startX = touch.clientX;
    startY = touch.clientY;
    active = false;
    lastTarget = null;

    clearHold();
    holdTimer = setTimeout(function () {
      holdTimer = null;
      if (dragSrc && !active) beginDrag(touch);
    }, HOLD_MS);
  }

  function onTouchMove(e) {
    if (pressTouchId == null) return;
    var touch = null;
    for (var i = 0; i < e.touches.length; i++) {
      if (e.touches[i].identifier === pressTouchId) {
        touch = e.touches[i];
        break;
      }
    }
    if (!touch) return;

    var dx = touch.clientX - startX;
    var dy = touch.clientY - startY;

    if (!active) {
      if (Math.abs(dx) > THRESHOLD || Math.abs(dy) > THRESHOLD) {
        clearHold();
        beginDrag(touch);
      } else {
        return;
      }
    }

    if (!active) return;
    e.preventDefault();
    moveAvatar(touch);

    var over = elementFromTouch(touch);
    var dropTarget = over;
    while (dropTarget && dropTarget !== document.body) {
      // Prefer the nearest droppable region we care about
      if (dropTarget.classList &&
          (dropTarget.classList.contains('slot') ||
           dropTarget.classList.contains('waiting') ||
           dropTarget.classList.contains('chip'))) {
        break;
      }
      dropTarget = dropTarget.parentElement;
    }
    if (!dropTarget || dropTarget === document.body) dropTarget = over;

    if (dropTarget !== lastTarget) {
      if (lastTarget) dispatch(lastTarget, 'dragleave', touch);
      lastTarget = dropTarget;
      if (lastTarget) dispatch(lastTarget, 'dragenter', touch);
    }
    if (lastTarget) dispatch(lastTarget, 'dragover', touch);
  }

  function onTouchEnd(e) {
    if (pressTouchId == null) return;
    var touch = null;
    for (var i = 0; i < e.changedTouches.length; i++) {
      if (e.changedTouches[i].identifier === pressTouchId) {
        touch = e.changedTouches[i];
        break;
      }
    }
    if (!touch) return;

    if (active) e.preventDefault();
    endDrag(touch, false);
  }

  function onTouchCancel() {
    endDrag({ clientX: 0, clientY: 0 }, true);
  }

  document.addEventListener('touchstart', onTouchStart, { passive: true, capture: true });
  document.addEventListener('touchmove', onTouchMove, { passive: false, capture: true });
  document.addEventListener('touchend', onTouchEnd, { passive: false, capture: true });
  document.addEventListener('touchcancel', onTouchCancel, { capture: true });

  // Kill iOS/Android long-press callout on chips without blocking buttons
  document.addEventListener('contextmenu', function (e) {
    if (closestDraggable(e.target) && !isInteractive(e.target)) {
      e.preventDefault();
    }
  }, true);
})();
