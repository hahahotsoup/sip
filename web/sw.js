/* sip service worker — version 1 (2026-01) */
/* 存在意义只有一个：让浏览器认为 sip 可以安装（可安装性要求注册一个带 fetch 处理器的 SW）。
   因此这里**刻意不缓存任何东西**：sip 的库数据由本地服务器实时生成，一旦被 SW 缓存下来，
   用户看到的就会是过期的旧库 —— 那比「装不上 PWA」糟糕得多。故 fetch 一律直通网络。 */

self.addEventListener("install", e => self.skipWaiting());

self.addEventListener("activate", e => e.waitUntil(self.clients.claim()));

self.addEventListener("fetch", e => {
  const req = e.request;
  // 只处理同源 GET；其余（POST、跨源等）什么都不做，交回浏览器默认行为。
  if (req.method !== "GET") return;
  if (new URL(req.url).origin !== self.location.origin) return;
  // 直通网络，绝不落入 Cache Storage：宁可慢一点，也不给用户看旧数据。
  e.respondWith(fetch(e.request));
});
