// Vanilla JS chat client. All URLs are relative, so the UI can be mounted at any path.
// The backend is called via UJMW (POST + JSON wrapper). Long-running methods (SendMessage, CompressChatContext)
// use the UJMW long-running protocol: initiate with '?wait=', poll via GET (respecting 'pollNbf'),
// cancel via DELETE. Servers which do not know the protocol answer synchronously (safe fallback).
(function () {
  "use strict";

  const backendUrl = "chatApi/backend/";
  const settingsUrl = "chatApi/settings/";
  const initialWaitSeconds = 2;

  const state = {
    chatId: null,
    busy: false,
    currentOperation: null,
    providers: [],
    settings: null
  };

  const el = (id) => document.getElementById(id);

  // ---------------------------------------------------------------------------
  // UJMW transport
  // ---------------------------------------------------------------------------

  async function sendRaw(method, url, body) {

    const init = { method: method, headers: {} };

    if (body !== undefined && body !== null) {
      init.headers["Content-Type"] = "application/json";
      init.body = JSON.stringify(body);
    }

    const response = await fetch(url, init);
    const text = await response.text();
    let json = null;

    if (text) {
      try {
        json = JSON.parse(text);
      } catch (e) {
        throw new Error("HTTP " + response.status + ": " + text.substring(0, 500));
      }
    }

    if (json && json.fault) {
      throw new Error(json.fault);
    }

    if (!response.ok) {
      throw new Error("HTTP " + response.status + " " + response.statusText);
    }

    if (json === null) {
      json = {};
    }

    return json;
  }

  async function call(baseUrl, methodName, args) {
    const wrapper = await sendRaw("POST", baseUrl + methodName, args || {});
    return wrapper["return"];
  }

  function randomString(length) {
    const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    const bytes = new Uint8Array(length);
    crypto.getRandomValues(bytes);
    let result = "";
    for (let i = 0; i < length; i++) {
      result += chars[bytes[i] % chars.length];
    }
    return result;
  }

  async function computePollingHash(pollingSecret, pollingSalt) {
    const data = new TextEncoder().encode(pollingSecret + pollingSalt);
    const hashBuffer = await crypto.subtle.digest("SHA-256", data);
    const hashBytes = new Uint8Array(hashBuffer);
    let binary = "";
    for (let i = 0; i < hashBytes.length; i++) {
      binary += String.fromCharCode(hashBytes[i]);
    }
    return btoa(binary).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  }

  async function buildControlUrl(url, operation, waitSeconds) {
    const salt = randomString(6);
    const hash = await computePollingHash(operation.pollingSecret, salt);
    let result = url + "?asyncId=" + encodeURIComponent(operation.asyncId) +
      "&pollingSalt=" + encodeURIComponent(salt) +
      "&pollingHash=" + encodeURIComponent(hash);
    if (waitSeconds !== undefined) {
      result += "&wait=" + waitSeconds;
    }
    return result;
  }

  function sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // Calls a UJMW long-running method. 'operationHolder.cancel()' can be used to request cancellation.
  async function callLongRunning(baseUrl, methodName, args, operationHolder) {

    const url = baseUrl + methodName;
    let wrapper = await sendRaw("POST", url + "?wait=" + initialWaitSeconds, args || {});

    const operation = { asyncId: null, pollingSecret: null, cancelRequested: false };

    if (wrapper.async && wrapper.async.asyncId) {
      operation.asyncId = wrapper.async.asyncId;
      operation.pollingSecret = wrapper.async.pollingSecret;
    }

    if (operationHolder) {
      operationHolder.cancel = async () => {
        if (!operation.asyncId || operation.cancelRequested) {
          return;
        }
        operation.cancelRequested = true;
        const cancelUrl = await buildControlUrl(url, operation, 1);
        await sendRaw("DELETE", cancelUrl, null);
      };
    }

    while (wrapper.async && wrapper.async.asyncId !== undefined && wrapper.async.asyncId !== null) {

      const execState = wrapper.async.execState;

      if (execState === 2 || execState === 3 || execState === -2) {
        break;
      }

      let delay = 500;
      if (wrapper.async.pollNbf) {
        const nbf = Date.parse(wrapper.async.pollNbf);
        if (!isNaN(nbf)) {
          delay = Math.max(100, nbf - Date.now());
        }
      }
      await sleep(Math.min(delay, 5000));

      const pollUrl = await buildControlUrl(url, operation);
      const polled = await sendRaw("GET", pollUrl, null);

      // keep the async id (only the initiating response carries the secret)
      if (polled.async) {
        polled.async.asyncId = operation.asyncId;
      }
      wrapper = polled;
    }

    if (wrapper.async && wrapper.async.execState === -2) {
      throw new Error("The operation was cancelled.");
    }

    return wrapper["return"];
  }

  // ---------------------------------------------------------------------------
  // Rendering
  // ---------------------------------------------------------------------------

  function escapeHtml(text) {
    return String(text)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  // minimal markdown: fenced code blocks, inline code, bold, line breaks
  function renderMarkdown(text) {
    const parts = String(text || "").split("```");
    let html = "";
    for (let i = 0; i < parts.length; i++) {
      if (i % 2 === 1) {
        let code = parts[i];
        const firstBreak = code.indexOf("\n");
        if (firstBreak >= 0 && firstBreak < 30) {
          code = code.substring(firstBreak + 1);
        }
        html += "<pre><code>" + escapeHtml(code) + "</code></pre>";
      } else {
        html += escapeHtml(parts[i])
          .replace(/`([^`]+)`/g, "<code>$1</code>")
          .replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>")
          .replace(/\n/g, "<br>");
      }
    }
    return html;
  }

  function appendMessage(role, author, content, extraClass) {
    const div = document.createElement("div");
    div.className = "message " + role + (extraClass ? " " + extraClass : "");
    div.innerHTML = "<div class=\"author\">" + escapeHtml(author || role) + "</div>" +
      "<div class=\"content\">" + renderMarkdown(content) + "</div>";
    el("messages").appendChild(div);
    el("messages").scrollTop = el("messages").scrollHeight;
    return div;
  }

  function showError(error) {
    const message = (error && error.message) ? error.message : String(error);
    appendMessage("error", "Error", message);
  }

  function setStatus(text) {
    el("status").textContent = text || "";
  }

  function setBusy(busy, cancellable) {
    state.busy = busy;
    el("btnSend").disabled = busy;
    el("btnCancel").hidden = !(busy && cancellable);
    el("btnNewChat").disabled = busy;
    updateHeaderButtons();
  }

  function updateHeaderButtons() {
    const hasChat = !!state.chatId;
    el("btnRename").disabled = !hasChat || state.busy;
    el("btnCompress").disabled = !hasChat || state.busy;
    el("btnDelete").disabled = !hasChat || state.busy;
  }

  // ---------------------------------------------------------------------------
  // Chats
  // ---------------------------------------------------------------------------

  async function loadChatList() {
    const chats = (await call(backendUrl, "GetRecentChats", {})) || [];
    const list = el("chatList");
    list.innerHTML = "";
    chats.forEach((chat) => {
      const li = document.createElement("li");
      li.textContent = chat.name || chat.id;
      li.title = (chat.name || chat.id) + " (" + new Date(chat.lastAction).toLocaleString() + ")";
      if (chat.id === state.chatId) {
        li.className = "active";
      }
      li.addEventListener("click", () => {
        if (!state.busy) {
          openChat(chat.id, chat.name);
        }
      });
      list.appendChild(li);
    });
    return chats;
  }

  async function openChat(chatId, chatName) {
    state.chatId = chatId;
    el("chatTitle").textContent = chatName || "Chat";
    el("messages").innerHTML = "";
    updateHeaderButtons();

    const count = await call(backendUrl, "GetChatMessageCount", { chatId: chatId });
    if (count > 0) {
      const messages = (await call(backendUrl, "GetChatMessages", { chatId: chatId, startIndex: 0, endIndex: count - 1 })) || [];
      messages.forEach((m) => appendMessage(m.role, m.author, m.content));
    }
    await loadChatList();
    el("txtMessage").focus();
  }

  async function newChat() {
    state.chatId = null;
    el("chatTitle").textContent = "New chat";
    el("messages").innerHTML = "";
    updateHeaderButtons();
    await loadChatList();
    el("txtMessage").focus();
  }

  async function sendMessage() {

    const text = el("txtMessage").value.trim();
    if (!text || state.busy) {
      return;
    }

    if (!state.settings || !state.settings.isReady) {
      openSettings();
      return;
    }

    setBusy(true, true);
    setStatus("Waiting for the answer...");

    try {

      if (!state.chatId) {
        state.chatId = await call(backendUrl, "BeginNewChat", { name: "" });
      }

      el("txtMessage").value = "";
      appendMessage("user", "You", text);
      const pending = appendMessage("assistant", "...", "thinking...", "pending");

      const holder = {};
      state.currentOperation = holder;

      try {
        const answer = await callLongRunning(backendUrl, "SendMessage", { chatId: state.chatId, message: text }, holder);
        pending.remove();
        if (answer) {
          appendMessage(answer.role || "assistant", answer.author, answer.content);
        }
      } catch (e) {
        pending.remove();
        throw e;
      }

      const chats = await loadChatList();
      const current = chats.find((c) => c.id === state.chatId);
      if (current) {
        el("chatTitle").textContent = current.name;
      }

    } catch (e) {
      showError(e);
    } finally {
      state.currentOperation = null;
      setBusy(false, false);
      setStatus("");
      el("txtMessage").focus();
    }
  }

  async function cancelOperation() {
    if (state.currentOperation && state.currentOperation.cancel) {
      setStatus("Cancelling...");
      try {
        await state.currentOperation.cancel();
      } catch (e) {
        showError(e);
      }
    }
  }

  async function renameChat() {
    if (!state.chatId) {
      return;
    }
    const newName = prompt("New name:", el("chatTitle").textContent);
    if (!newName) {
      return;
    }
    try {
      await call(backendUrl, "RenameChat", { chatId: state.chatId, newName: newName });
      el("chatTitle").textContent = newName;
      await loadChatList();
    } catch (e) {
      showError(e);
    }
  }

  async function deleteChat() {
    if (!state.chatId || !confirm("Delete this chat?")) {
      return;
    }
    try {
      await call(backendUrl, "DeleteChat", { chatId: state.chatId });
      await newChat();
    } catch (e) {
      showError(e);
    }
  }

  async function compressChat() {
    if (!state.chatId) {
      return;
    }
    setBusy(true, true);
    setStatus("Compressing the chat context...");
    try {
      const holder = {};
      state.currentOperation = holder;
      await callLongRunning(backendUrl, "CompressChatContext", { chatId: state.chatId }, holder);
      setStatus("Context compressed.");
    } catch (e) {
      showError(e);
    } finally {
      state.currentOperation = null;
      setBusy(false, false);
    }
  }

  // ---------------------------------------------------------------------------
  // Settings
  // ---------------------------------------------------------------------------

  async function loadSettings() {
    state.providers = (await call(settingsUrl, "GetAvailableProviders", {})) || [];
    state.settings = await call(settingsUrl, "GetSettings", {});
    renderProviderInfo();
  }

  function renderProviderInfo() {
    const s = state.settings;
    if (!s || !s.providerName) {
      el("providerInfo").textContent = "No LLM provider configured.";
    } else if (!s.isReady) {
      el("providerInfo").textContent = s.providerName + " " + s.model + " - API key missing!";
    } else if (!s.hasApiKey) {
      el("providerInfo").textContent = s.providerName + " " + s.model;
    } else {
      el("providerInfo").textContent = s.providerName + " " + s.model + " (key " + (s.apiKeyHint || "") + ")";
    }
  }

  function findProvider(name) {
    return state.providers.find((p) => p.name === name);
  }

  function fillSettingsDialog(providerName) {
    const s = state.settings || {};
    const provider = findProvider(providerName);
    const isCurrent = (providerName === s.providerName);

    el("txtModel").value = isCurrent ? (s.model || "") : "";
    el("txtModel").placeholder = provider ? (provider.defaultModel || "(default)") : "(default)";
    el("txtApiKey").value = "";

    if (isCurrent && s.hasApiKey) {
      el("apiKeyState").textContent = "Stored API key: " + (s.apiKeyHint || "****") + " (leave empty to keep it)";
    } else {
      el("apiKeyState").textContent = "No API key stored for this provider.";
    }

    renderCapabilities(providerName);
  }

  // Renders one switch per capability (opt-out of native provider features). Unavailable features are shown
  // with an X: the Layer-2 harness emulates them (or they are simply not supported).
  async function renderCapabilities(providerName) {
    const container = el("capabilityList");
    container.innerHTML = "";
    const provider = findProvider(providerName);
    const capabilities = (provider && Array.isArray(provider.capabilities)) ? provider.capabilities : [];

    if (capabilities.length === 0) {
      container.textContent = "(no information)";
      return;
    }

    let disabled = [];
    try {
      disabled = (await call(settingsUrl, "GetDisabledCapabilities", { providerName: providerName })) || [];
    } catch (e) {
      disabled = [];
    }

    if (el("selProvider").value !== providerName) {
      return;
    }

    capabilities.forEach((c) => {
      const row = document.createElement("label");
      row.className = "capability" + (c.isAvailable ? "" : " unavailable");
      row.title = c.description || "";

      if (c.isAvailable) {
        const input = document.createElement("input");
        input.type = "checkbox";
        input.className = "switch";
        input.dataset.capability = c.name;
        input.checked = !disabled.some((d) => (d || "").toLowerCase() === (c.name || "").toLowerCase());
        row.appendChild(input);
      } else {
        const x = document.createElement("span");
        x.className = "capability-x";
        x.textContent = "\u2716";
        x.title = "Not available for this provider";
        row.appendChild(x);
      }

      const text = document.createElement("span");
      text.textContent = c.displayName || c.name;
      row.appendChild(text);

      container.appendChild(row);
    });
  }

  function collectDisabledCapabilities() {
    const result = [];
    el("capabilityList").querySelectorAll("input[data-capability]").forEach((input) => {
      if (!input.checked) {
        result.push(input.dataset.capability);
      }
    });
    return result;
  }

  function openSettings() {
    const select = el("selProvider");
    select.innerHTML = "";
    state.providers.forEach((p) => {
      const option = document.createElement("option");
      option.value = p.name;
      option.textContent = p.displayName || p.name;
      select.appendChild(option);
    });

    const current = (state.settings && state.settings.providerName) ? state.settings.providerName : (state.providers[0] ? state.providers[0].name : "");
    select.value = current;
    fillSettingsDialog(current);

    el("settingsDialog").hidden = false;
    el("txtApiKey").focus();
  }

  function closeSettings() {
    el("settingsDialog").hidden = true;
    el("txtApiKey").value = "";
  }

  async function saveSettings() {
    try {
      const providerName = el("selProvider").value;
      await call(settingsUrl, "SaveSettings", {
        providerName: providerName,
        model: el("txtModel").value.trim(),
        apiKey: el("txtApiKey").value.trim()
      });
      await call(settingsUrl, "SaveDisabledCapabilities", {
        providerName: providerName,
        disabledCapabilityNames: collectDisabledCapabilities()
      });
      closeSettings();
      await loadSettings();
    } catch (e) {
      alert(e.message || e);
    }
  }

  async function deleteApiKey() {
    if (!confirm("Delete the stored API key?")) {
      return;
    }
    try {
      await call(settingsUrl, "DeleteApiKey", { providerName: el("selProvider").value });
      await loadSettings();
      fillSettingsDialog(el("selProvider").value);
    } catch (e) {
      alert(e.message || e);
    }
  }

  // ---------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------

  function wire() {
    el("btnSend").addEventListener("click", sendMessage);
    el("btnCancel").addEventListener("click", cancelOperation);
    el("btnNewChat").addEventListener("click", newChat);
    el("btnRename").addEventListener("click", renameChat);
    el("btnDelete").addEventListener("click", deleteChat);
    el("btnCompress").addEventListener("click", compressChat);
    el("btnSettings").addEventListener("click", openSettings);
    el("btnSettingsCancel").addEventListener("click", closeSettings);
    el("btnSettingsSave").addEventListener("click", saveSettings);
    el("btnDeleteKey").addEventListener("click", deleteApiKey);
    el("selProvider").addEventListener("change", () => fillSettingsDialog(el("selProvider").value));

    el("txtMessage").addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
      }
    });
  }

  async function init() {
    wire();
    updateHeaderButtons();
    try {
      await loadSettings();
      await loadChatList();
      if (!state.settings || !state.settings.isReady) {
        openSettings();
      }
    } catch (e) {
      showError(e);
    }
  }

  init();

})();
