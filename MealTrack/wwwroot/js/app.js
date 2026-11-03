/* MealTrack front-end helpers (no framework). */
(function () {
  "use strict";

  function icons() {
    if (window.lucide) window.lucide.createIcons();
  }

  // ---------- Confirm dialogs: <form data-confirm="Are you sure?"> ----------
  document.addEventListener("submit", function (e) {
    var form = e.target;
    var msg = form.getAttribute("data-confirm");
    if (msg && !window.confirm(msg)) e.preventDefault();
  });

  // ---------- Quantity steppers & live totals ----------
  function qtyInputs(scope) {
    return Array.prototype.slice.call((scope || document).querySelectorAll("input.qty-input"));
  }

  function clamp(v, min, max) {
    return Math.max(min, Math.min(max, v));
  }

  function markChanged(input) {
    var wrap = input.closest(".qty-stepper");
    if (!wrap) return;
    var original = input.getAttribute("data-original") || "";
    wrap.classList.toggle("changed", input.value !== original);
  }

  function updateTotals() {
    var form = document.getElementById("countForm");
    if (!form) return;
    var grand = 0, filled = 0, changed = 0;
    qtyInputs(form).forEach(function (inp) {
      if (inp.value !== "") {
        grand += parseInt(inp.value, 10) || 0;
        filled += 1;
      }
      if (!inp.disabled && inp.value !== (inp.getAttribute("data-original") || "")) changed += 1;
    });
    document.querySelectorAll("[data-day-total]").forEach(function (el) {
      var day = el.getAttribute("data-day-total");
      var sum = 0;
      qtyInputs(form).forEach(function (inp) {
        if (inp.getAttribute("data-day") === day && inp.value !== "") sum += parseInt(inp.value, 10) || 0;
      });
      el.textContent = sum;
    });
    var t = document.getElementById("liveTotal");
    if (t) t.textContent = grand;
    var c = document.getElementById("changedCount");
    if (c) c.textContent = changed ? changed + " unsaved change" + (changed > 1 ? "s" : "") : "No unsaved changes";
    var btn = document.getElementById("submitCounts");
    if (btn) btn.disabled = changed === 0;
  }

  document.addEventListener("click", function (e) {
    var stepBtn = e.target.closest("[data-step]");
    if (stepBtn) {
      var input = stepBtn.closest(".qty-stepper").querySelector("input");
      if (input.disabled) return;
      var max = parseInt(input.getAttribute("max"), 10) || 20;
      var current = input.value === "" ? 0 : parseInt(input.value, 10) || 0;
      var next = input.value === "" && stepBtn.getAttribute("data-step") === "1" ? 1
        : clamp(current + parseInt(stepBtn.getAttribute("data-step"), 10), 0, max);
      input.value = next;
      markChanged(input);
      updateTotals();
      return;
    }

    var fill = e.target.closest("[data-fill]");
    if (fill) {
      var mode = fill.getAttribute("data-fill"); // "zero" | "last" | "one" | "clear"
      var scopeSel = fill.getAttribute("data-scope");
      var scope = scopeSel ? document.querySelector(scopeSel) : document.getElementById("countForm");
      var applied = 0;
      qtyInputs(scope).forEach(function (inp) {
        if (inp.disabled) return;
        if (mode === "zero") inp.value = 0;
        else if (mode === "one") inp.value = 1;
        else if (mode === "clear") inp.value = inp.getAttribute("data-original") || "";
        else if (mode === "last") {
          var last = inp.getAttribute("data-last");
          if (last === null || last === "") return;
          inp.value = last;
        }
        applied += 1;
        markChanged(inp);
      });
      if (mode === "last" && applied === 0) window.alert("No submissions from last week were found for these meals.");
      updateTotals();
    }
  });

  document.addEventListener("input", function (e) {
    if (!e.target.classList.contains("qty-input")) return;
    var inp = e.target;
    var max = parseInt(inp.getAttribute("max"), 10) || 20;
    if (inp.value !== "") {
      var v = parseInt(inp.value, 10);
      inp.value = isNaN(v) ? "" : clamp(v, 0, max);
    }
    markChanged(inp);
    updateTotals();
  });

  // Warn before leaving with unsaved count changes
  var dirtySubmit = false;
  document.addEventListener("submit", function (e) {
    if (e.target.id === "countForm") dirtySubmit = true;
  });
  window.addEventListener("beforeunload", function (e) {
    var form = document.getElementById("countForm");
    if (!form || dirtySubmit) return;
    var changed = qtyInputs(form).some(function (inp) {
      return !inp.disabled && inp.value !== (inp.getAttribute("data-original") || "");
    });
    if (changed) {
      e.preventDefault();
      e.returnValue = "";
    }
  });

  // ---------- Countdown: <span data-countdown="2026-11-03T20:00:00"> ----------
  function tickCountdowns() {
    var now = Date.now();
    document.querySelectorAll("[data-countdown]").forEach(function (el) {
      var target = new Date(el.getAttribute("data-countdown")).getTime();
      var diff = Math.floor((target - now) / 1000);
      if (isNaN(target)) return;
      if (diff <= 0) {
        el.textContent = "Closed";
        return;
      }
      var d = Math.floor(diff / 86400), h = Math.floor((diff % 86400) / 3600), m = Math.floor((diff % 3600) / 60);
      el.textContent = (d ? d + "d " : "") + (d || h ? h + "h " : "") + m + "m";
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    icons();
    updateTotals();
    tickCountdowns();
    setInterval(tickCountdowns, 30000);
  });
})();
