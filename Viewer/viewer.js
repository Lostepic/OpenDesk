"use strict";

let [display, standbyDisplay] = document.querySelectorAll(".display-frame");
const indicator = document.querySelector(".connection");
const statusLabel = document.querySelector("#status");
const resolution = document.querySelector("#resolution");
const notice = document.querySelector("#notice");
const heading = document.querySelector("#heading");
const detail = document.querySelector("#detail");
const retry = document.querySelector("#retry");
const fullscreen = document.querySelector("#fullscreen");
const disconnect = document.querySelector("#disconnect");
const revealControls = document.querySelector("#reveal-controls");
const stage = document.querySelector("#stage");
const viewer = document.documentElement;
const android = navigator.userAgent.includes("OpenDeskAndroid/");
let socket;
let hideControls;
const pointers = new Set();
let sendingTouch = false;
const pendingTouches = [];

if (android) {
  viewer.classList.add("android-viewer");
  const peer = document.querySelector("#peer");
  peer.textContent = location.hostname;
  peer.hidden = false;
  disconnect.hidden = false;
  revealControls.hidden = false;
  disconnect.addEventListener("click", () => leaveAndroid("disconnect"));
  revealControls.addEventListener("click", () => showControls());
  document.addEventListener("keydown", () => showControls());
  stage.addEventListener("pointerdown", startTouch);
  stage.addEventListener("pointermove", event => updateTouch("m", event));
  stage.addEventListener("pointerup", event => updateTouch("u", event));
  stage.addEventListener("pointercancel", event => updateTouch("c", event));
  stage.addEventListener("lostpointercapture", event => updateTouch("c", event));
}

function touchPoint(event, clamp) {
  if (display.hidden || !display.naturalWidth)
    return null;
  const rect = display.getBoundingClientRect();
  const scale = Math.min(rect.width / display.naturalWidth, rect.height / display.naturalHeight);
  const width = display.naturalWidth * scale;
  const height = display.naturalHeight * scale;
  if (!width || !height)
    return null;
  const x = (event.clientX - rect.left - (rect.width - width) / 2) / width;
  const y = (event.clientY - rect.top - (rect.height - height) / 2) / height;
  if (!clamp && (x < 0 || x > 1 || y < 0 || y > 1))
    return null;
  return [x, y].map(value => Math.round(65535 * Math.max(0, Math.min(1, value))));
}

function startTouch(event) {
  if (pointers.size >= 5 || pointers.has(event.pointerId) || socket?.readyState !== WebSocket.OPEN)
    return;
  const point = touchPoint(event, false);
  if (!point)
    return;
  event.preventDefault();
  stage.setPointerCapture(event.pointerId);
  pointers.add(event.pointerId);
  queueTouch("d", event.pointerId, point);
}

function updateTouch(phase, event) {
  if (!pointers.has(event.pointerId))
    return;
  if (phase !== "m")
    pointers.delete(event.pointerId);
  const point = touchPoint(event, true);
  if (phase === "c" || !point)
    queueTouch("c", event.pointerId, [0, 0]);
  else
    queueTouch(phase, event.pointerId, point);
}

function queueTouch(phase, id, point) {
  if (phase === "m") {
    for (let i = pendingTouches.length - 1; i >= 0 && pendingTouches[i].phase === "m"; i--) {
      if (pendingTouches[i].id === id) {
        pendingTouches.splice(i, 1);
        break;
      }
    }
  }
  pendingTouches.push({ phase, id, point });
  void sendTouches();
}

async function sendTouches() {
  if (sendingTouch)
    return;
  sendingTouch = true;
  try {
    while (pendingTouches.length && socket?.readyState === WebSocket.OPEN) {
      const { phase, id, point } = pendingTouches.shift();
      const response = await fetch("/touch", {
        method: "POST",
        headers: { "X-OpenDesk-Key": location.hash.slice(1) },
        body: [phase, id, ...point].join(" ")
      });
      if (!response.ok && response.status !== 409)
        break;
    }
  } catch {
    // A lost connection releases the touch contact on the host.
  } finally {
    pendingTouches.length = 0;
    sendingTouch = false;
  }
}

function showControls() {
  viewer.classList.remove("controls-hidden");
  clearTimeout(hideControls);
  if (android && (viewer.classList.contains("viewer-fit") || document.fullscreenElement)) {
    hideControls = setTimeout(() => viewer.classList.add("controls-hidden"), 4500);
  }
}

function leaveAndroid(reason) {
  const connection = socket;
  socket = undefined;
  connection?.close();
  releaseFrame();
  showControls();
  location.href = `opendesk://${reason}`;
}

function show(state, status, title, message, reconnect = false) {
  indicator.dataset.state = state;
  statusLabel.textContent = status;
  if (state === "live") {
    notice.hidden = true;
    display.hidden = false;
    return;
  }
  heading.textContent = title;
  detail.textContent = message;
  retry.hidden = !reconnect;
  notice.hidden = false;
  display.hidden = true;
  resolution.hidden = true;
}

function releaseFrame() {
  pointers.clear();
  pendingTouches.length = 0;
  for (const frame of [display, standbyDisplay]) {
    const url = frame.getAttribute("src");
    if (url) URL.revokeObjectURL(url);
    frame.removeAttribute("src");
    frame.hidden = true;
  }
}

function connect() {
  if (socket) {
    const previous = socket;
    socket = undefined;
    previous.close();
  }
  releaseFrame();
  const key = location.hash.slice(1);
  if (!/^[a-f0-9]{32}$/.test(key)) {
    show("offline", "Waiting", "Open a viewer link",
      "Copy a viewer link from OpenDesk on your Windows PC and open it here.");
    return;
  }

  show("connecting", "Connecting", "Connecting to your PC", "Waiting for the first frame.");
  const connection = new WebSocket(`wss://${location.host}/stream`);
  socket = connection;
  let receiveTimeout;
  const expectFrame = () => {
    clearTimeout(receiveTimeout);
    receiveTimeout = setTimeout(() => connection.close(), 12000);
  };
  expectFrame();
  connection.addEventListener("open", () => connection.send(key));
  let pendingFrame;
  let decoding = false;
  connection.addEventListener("message", async ({ data }) => {
    if (socket !== connection)
      return;
    expectFrame();
    if (!(data instanceof Blob))
      return;

    pendingFrame = data;
    connection.send("1");
    if (decoding)
      return;
    decoding = true;
    while (pendingFrame && socket === connection && connection.readyState === WebSocket.OPEN) {
      const nextUrl = URL.createObjectURL(new Blob([pendingFrame], { type: "image/jpeg" }));
      pendingFrame = null;
      const nextDisplay = standbyDisplay;
      nextDisplay.src = nextUrl;
      try {
        await nextDisplay.decode();
        if (socket !== connection || connection.readyState !== WebSocket.OPEN)
          return;
        const oldUrl = display.getAttribute("src");
        if (oldUrl) URL.revokeObjectURL(oldUrl);
        display.removeAttribute("src");
        display.hidden = true;
        standbyDisplay = display;
        display = nextDisplay;
        if (indicator.dataset.state !== "live")
          show("live", "Live");
        else
          display.hidden = false;
        const dimensions = `${display.naturalWidth} × ${display.naturalHeight}`;
        if (resolution.textContent !== dimensions)
          resolution.textContent = dimensions;
        resolution.hidden = false;
      } catch {
        connection.close();
      } finally {
        if (socket !== connection || display !== nextDisplay)
          URL.revokeObjectURL(nextUrl);
      }
    }
    decoding = false;
  });
  connection.addEventListener("close", ({ code }) => {
    clearTimeout(receiveTimeout);
    if (socket !== connection)
      return;
    if (android) {
      leaveAndroid("connection-ended");
      return;
    }
    socket = undefined;
    releaseFrame();
    if (code === 1008) {
      show("offline", "Invite expired", "This link is no longer valid",
        "Copy the current viewer link from OpenDesk on your Windows PC.");
    } else {
      show("offline", "Disconnected", "Viewer disconnected",
        "Check that OpenDesk is sharing and that both devices are on the same network.", true);
    }
  });
}

retry.addEventListener("click", connect);
window.addEventListener("hashchange", connect);

function updateFullscreenButton() {
  if (viewer.classList.contains("viewer-fit"))
    fullscreen.textContent = "Exit fitted view";
  else if (document.fullscreenElement)
    fullscreen.textContent = "Exit full screen";
  else
    fullscreen.textContent = document.fullscreenEnabled ? "Full screen" : "Fit to window";
}

fullscreen.hidden = false;
fullscreen.addEventListener("click", async () => {
  if (viewer.classList.contains("viewer-fit")) {
    viewer.classList.remove("viewer-fit");
  } else if (document.fullscreenElement) {
    await document.exitFullscreen();
  } else {
    try {
      if (document.fullscreenEnabled)
        await viewer.requestFullscreen();
      else
        viewer.classList.add("viewer-fit");
    } catch {
      viewer.classList.add("viewer-fit");
    }
  }
  updateFullscreenButton();
  showControls();
});
document.addEventListener("fullscreenchange", () => {
  updateFullscreenButton();
  showControls();
});
updateFullscreenButton();

connect();
