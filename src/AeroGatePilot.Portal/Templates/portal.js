(function () {
  document.querySelectorAll("[data-reveal]").forEach(function (button) {
    button.addEventListener("click", function () {
      var input = button.parentElement.querySelector("input");
      input.type = input.type === "password" ? "text" : "password";
      input.focus();
    });
  });

  var pollUrl = document.body.getAttribute("data-status-poll");
  if (!pollUrl || !window.fetch) return;

  function refresh() {
    fetch(pollUrl, { cache: "no-store" })
      .then(function (r) { return r.json(); })
      .then(function (data) {
        if (!data.connected) {
          window.location.href = "/portal/";
          return;
        }
        document.querySelectorAll("[data-field]").forEach(function (el) {
          var key = el.getAttribute("data-field");
          if (data[key] !== undefined) el.textContent = data[key];
        });
        document.querySelectorAll("[data-field-width]").forEach(function (el) {
          var key = el.getAttribute("data-field-width");
          if (data[key] !== undefined) el.style.width = data[key] + "%";
        });
      })
      .catch(function () { });
  }

  setInterval(refresh, 5000);
})();
