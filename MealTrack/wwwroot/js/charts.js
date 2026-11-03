/* MealTrack charts: Chart.js in the 4-colour palette (green, black, grey + opacity tints). */
(function () {
  "use strict";
  if (!window.Chart) return;

  var GREEN = "22, 163, 74", BLACK = "23, 23, 23", GREY = "115, 115, 115";
  function c(rgb, a) { return "rgba(" + rgb + "," + (a === undefined ? 1 : a) + ")"; }
  var SERIES = [c(GREEN), c(BLACK), c(GREY), c(GREEN, .45), c(BLACK, .45), c(GREY, .45), c(GREEN, .25), c(GREY, .25)];

  Chart.defaults.font.family = '"Inter", system-ui, sans-serif';
  Chart.defaults.color = c(GREY);
  Chart.defaults.borderColor = c(GREY, .15);
  Chart.defaults.plugins.legend.labels.usePointStyle = true;
  Chart.defaults.plugins.legend.labels.boxWidth = 8;
  Chart.defaults.maintainAspectRatio = false;

  function load(el, url, build) {
    fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (data) { new Chart(el, build(data)); })
      .catch(function () { el.parentElement.innerHTML = '<div class="empty-state small">Chart data could not be loaded.</div>'; });
  }

  var builders = {
    consumption: function (d) {
      return { type: "bar", data: { labels: d.labels, datasets: d.datasets.map(function (s, i) {
        return { label: s.label, data: s.data, backgroundColor: SERIES[i % SERIES.length], borderRadius: 4, maxBarThickness: 28 };
      }) }, options: { scales: { x: { stacked: true, grid: { display: false } }, y: { stacked: true, beginAtZero: true, ticks: { precision: 0 } } } } };
    },
    "meal-split": function (d) {
      return { type: "doughnut", data: { labels: d.labels, datasets: [{ data: d.data, backgroundColor: SERIES, borderColor: "#FFFFFF", borderWidth: 2 }] },
        options: { cutout: "62%", plugins: { legend: { position: "bottom" } } } };
    },
    "top-foods": function (d) {
      return { type: "bar", data: { labels: d.labels, datasets: [{ label: "Portions", data: d.data, backgroundColor: c(GREEN, .85), borderRadius: 4, maxBarThickness: 18 }] },
        options: { indexAxis: "y", plugins: { legend: { display: false } }, scales: { x: { beginAtZero: true, ticks: { precision: 0 } }, y: { grid: { display: false } } } } };
    },
    preparation: function (d) {
      function line(label, data, color, dash) {
        return { label: label, data: data, borderColor: color, backgroundColor: color, borderDash: dash || [], tension: .3, pointRadius: 2 };
      }
      return { type: "line", data: { labels: d.labels, datasets: [
        line("Required", d.required, c(BLACK)), line("Prepared", d.prepared, c(GREEN)), line("Wasted", d.wasted, c(GREY), [4, 4])
      ] }, options: { scales: { y: { beginAtZero: true, ticks: { precision: 0 } }, x: { grid: { display: false } } } } };
    },
    wastage: function (d) {
      return { type: "line", data: { labels: d.labels, datasets: [{ label: "Wastage %", data: d.data, borderColor: c(GREEN),
        backgroundColor: c(GREEN, .12), fill: true, tension: .3, spanGaps: true, pointRadius: 2 }] },
        options: { plugins: { legend: { display: false } }, scales: { y: { beginAtZero: true, ticks: { callback: function (v) { return v + "%"; } } }, x: { grid: { display: false } } } } };
    }
  };

  document.addEventListener("DOMContentLoaded", function () {
    document.querySelectorAll("canvas[data-chart]").forEach(function (el) {
      var kind = el.getAttribute("data-chart");
      if (builders[kind]) load(el, el.getAttribute("data-src"), builders[kind]);
    });
  });
})();
