// Vanilla JS chat client. All URLs are relative, so the UI can be mounted at any path.
// The backend is called via UJMW (POST + JSON wrapper). Long-running methods (SendMessage, CompressChatContext)
// use the UJMW long-running protocol: initiate with '?wait=', poll via GET (respecting 'pollNbf'),
// cancel via DELETE. Servers which do not know the protocol answer synchronously (safe fallback).
(function () {
  "use strict";

  const backendUrl = "chatApi/backend/";
  const settingsUrl = "chatApi/settings/";
  const extensionsUrl = "chatApi/extensions/";
  const skillLevels = ["Hidden", "OnDemand", "InitializeAtStart"];
  const skillLevelNames = { Hidden: "Hidden", OnDemand: "On demand", InitializeAtStart: "Initialize at start" };
  const toolLevels = ["Hidden", "RequiresConfirmation", "NoConfirmation"];
  const toolLevelNames = { Hidden: "Hidden", RequiresConfirmation: "Requires confirmation", NoConfirmation: "No confirmation" };
  const initialWaitSeconds = 2;
  const pollIntervalMs = 1000;

  const state = {
    chatId: null,
    busy: false,
    currentOperation: null,
    providers: [],
    settings: null,
    theme: "light",
    userInstructions: "",
    skillSources: [],
    extensionsAvailable: false,
    toolSources: [],
    toolSourcesAvailable: false,
    details: null
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

  // Delivers the recorded UJMW callback invocations ('<callbackName>Invokes' arrays of { '#': n, 'arg1': ... })
  // to the given handlers. Already delivered invocations (by '#') are skipped. Unknown shapes are ignored.
  function dispatchCallbackInvokes(wrapper, callbacks, delivered) {
    if (!wrapper || !callbacks) {
      return;
    }
    Object.keys(callbacks).forEach((callbackName) => {
      const invokes = wrapper[callbackName + "Invokes"];
      const handler = callbacks[callbackName];
      if (!Array.isArray(invokes) || typeof handler !== "function") {
        return;
      }
      invokes.forEach((entry) => {
        if (!entry || typeof entry !== "object") {
          return;
        }
        const key = callbackName + "#" + entry["#"];
        if (entry["#"] !== undefined && delivered[key]) {
          return;
        }
        delivered[key] = true;
        const args = [];
        if (entry.arg1 !== undefined) {
          for (let i = 1; entry["arg" + i] !== undefined; i++) {
            args.push(entry["arg" + i]);
          }
        } else {
          Object.keys(entry).forEach((k) => {
            if (k !== "#") {
              args.push(entry[k]);
            }
          });
        }
        try {
          handler.apply(null, args);
        } catch (e) {
          // a failing UI handler must never break the operation
        }
      });
    });
  }

  // Calls a UJMW long-running method. 'operationHolder.cancel()' can be used to request cancellation.
  // 'callbacks' is an optional map (callbackName -> handler), for example { onProgress: (text, current, max) => ... }.
  async function callLongRunning(baseUrl, methodName, args, operationHolder, callbacks) {

    const url = baseUrl + methodName;
    const delivered = {};
    let wrapper = await sendRaw("POST", url + "?wait=" + initialWaitSeconds, args || {});
    dispatchCallbackInvokes(wrapper, callbacks, delivered);

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

      // fixed polling interval, but never earlier than the server-side 'pollNbf'
      let delay = pollIntervalMs;
      if (wrapper.async.pollNbf) {
        const nbf = Date.parse(wrapper.async.pollNbf);
        if (!isNaN(nbf)) {
          delay = Math.max(pollIntervalMs, nbf - Date.now());
        }
      }
      await sleep(Math.min(delay, 5000));

      const pollUrl = await buildControlUrl(url, operation);
      const polled = await sendRaw("GET", pollUrl, null);
      dispatchCallbackInvokes(polled, callbacks, delivered);

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
    el("btnDetails").disabled = !hasChat || state.busy;
  }

  // renders a persisted message (control/activity messages of the harness are shown as ghost bubbles)
  function appendChatMessage(m) {
    if (m.kind === "Control") {
      const notice = String(m.content || "").replace(/<\/?harness_notice>/g, "");
      return appendMessage("control", m.author || "Harness", notice);
    }
    if (m.kind === "Activity") {
      return appendMessage("control", m.author || "Harness", m.content, "activity");
    }
    return appendMessage(m.role || "assistant", m.author, m.content);
  }

  // convention: progress texts with this prefix announce an activity (rendered as live ghost bubble)
  const activityPrefix = "[activity] ";

  function formatProgress(text, current, max) {
    let result = text || "working...";
    if (typeof current === "number" && typeof max === "number" && max > 0) {
      result += " (" + current + "/" + max + ")";
    }
    return result;
  }

  // ---------------------------------------------------------------------------
  // Tool approvals (tools of sources with level 'RequiresConfirmation')
  // ---------------------------------------------------------------------------

  // Polls the pending tool approvals of the chat while a message is being sent and renders an approval card
  // for each of them. Backends which do not know 'GetPendingToolApprovals' are ignored (convention credo).
  function startApprovalPolling(chatId) {

    const cards = {};
    let stopped = false;

    async function poll() {
      while (!stopped) {
        let approvals = null;
        try {
          approvals = await call(backendUrl, "GetPendingToolApprovals", { chatId: chatId });
        } catch (e) {
          return;
        }
        const openIds = {};
        (Array.isArray(approvals) ? approvals : []).forEach((a) => {
          if (!a || !a.approvalId) {
            return;
          }
          openIds[a.approvalId] = true;
          if (!cards[a.approvalId]) {
            cards[a.approvalId] = renderApprovalCard(chatId, a);
          }
        });
        // requests which were answered elsewhere or have expired
        Object.keys(cards).forEach((id) => {
          if (!openIds[id] && cards[id].isConnected && !cards[id].dataset.answered) {
            closeApprovalCard(cards[id], "no longer pending");
          }
        });
        await sleep(pollIntervalMs);
      }
    }

    poll();

    return {
      stop: () => {
        stopped = true;
        Object.keys(cards).forEach((id) => {
          if (cards[id].isConnected && !cards[id].dataset.answered) {
            closeApprovalCard(cards[id], "no longer pending");
          }
        });
      }
    };
  }

  function renderApprovalCard(chatId, approval) {

    const sourceName = approval.sourceDisplayName || approval.sourceId || "";
    const kind = approval.kind || "ToolApproval";
    const isGuard = (kind === "PathAccess" || kind === "DomainAccess" || kind === "ShellCommand");
    const guardTitles = { PathAccess: "Path access", DomainAccess: "Domain access", ShellCommand: "Shell command" };
    const div = document.createElement("div");
    div.className = "message approval";

    if (isGuard) {
      div.innerHTML =
        "<div class=\"author\">" + escapeHtml(guardTitles[kind]) + " request" + (sourceName ? " (" + escapeHtml(sourceName) + ")" : "") + "</div>" +
        "<div class=\"content\">The tool <code>" + escapeHtml(approval.toolName || "?") + "</code> needs access to:" +
        "<pre><code>" + escapeHtml(approval.subject || "") + "</code></pre></div>" +
        "<div class=\"approval-actions\">" +
        "<button type=\"button\" data-answer=\"Deny\" title=\"Block this access\">Deny</button> " +
        "<button type=\"button\" class=\"primary\" data-answer=\"AllowOnce\" title=\"Allow only this access\">Allow once</button> " +
        "<button type=\"button\" data-answer=\"AllowPermanently\" title=\"Add it to the global allow-list of this tool source\">Allow permanently</button>" +
        "</div>";
    } else {
      div.innerHTML =
      "<div class=\"author\">Tool approval" + (sourceName ? " (" + escapeHtml(sourceName) + ")" : "") + "</div>" +
      "<div class=\"content\">The model wants to call <code>" + escapeHtml(approval.toolName || "?") + "</code>:" +
      "<pre><code>" + escapeHtml(approval.argumentsJson || "{}") + "</code></pre></div>" +
      "<div class=\"approval-actions\">" +
      "<button type=\"button\" data-answer=\"Deny\" title=\"Reject the call and hide this tool source for this chat\">Deny</button> " +
      "<button type=\"button\" class=\"primary\" data-answer=\"AllowOnce\" title=\"Execute only this call\">Allow once</button> " +
      "<button type=\"button\" data-answer=\"AllowForChat\" title=\"Execute all calls of this tool source within this chat without asking\">Allow for this chat</button>" +
      "</div>";
    }

    const resultTexts = {
      Deny: isGuard ? "denied" : "denied (tool source hidden for this chat)",
      AllowOnce: "allowed once",
      AllowForChat: "allowed for this chat",
      AllowPermanently: "allowed permanently (added to the allow-list)"
    };

    div.querySelectorAll("button").forEach((button) => {
      button.addEventListener("click", async () => {
        const decision = button.getAttribute("data-answer");
        div.dataset.answered = "1";
        div.querySelectorAll("button").forEach((b) => { b.disabled = true; });
        try {
          await call(backendUrl, "AnswerToolApproval", { chatId: chatId, approvalId: approval.approvalId, decision: decision });
          closeApprovalCard(div, resultTexts[decision] || decision);
        } catch (e) {
          showError(e);
        }
      });
    });

    el("messages").appendChild(div);
    el("messages").scrollTop = el("messages").scrollHeight;
    return div;
  }

  function closeApprovalCard(card, resultText) {
    const actions = card.querySelector(".approval-actions");
    if (actions) {
      actions.textContent = resultText;
      actions.className = "approval-actions muted small";
    }
    card.dataset.answered = "1";
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
      messages.forEach((m) => appendChatMessage(m));
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

      const callbacks = {
        onProgress: (statusText, current, max) => {
          if (typeof statusText === "string" && statusText.indexOf(activityPrefix) === 0) {
            // live ghost bubble in front of the pending answer
            const bubble = appendMessage("control", "Harness", statusText.substring(activityPrefix.length), "activity");
            el("messages").insertBefore(bubble, pending);
            return;
          }
          const progressText = formatProgress(statusText, current, max);
          setStatus(progressText);
          const content = pending.querySelector(".content");
          if (content) {
            content.textContent = progressText;
          }
        }
      };

      const approvalPolling = startApprovalPolling(state.chatId);

      try {
        const answer = await callLongRunning(backendUrl, "SendMessage", { chatId: state.chatId, message: text }, holder, callbacks);
        pending.remove();
        if (answer) {
          appendChatMessage(answer);
        }
      } catch (e) {
        pending.remove();
        throw e;
      } finally {
        approvalPolling.stop();
      }

      const chats = await loadChatList();
      const current = chats.find((c) => c.id === state.chatId);
      if (current) {
        el("chatTitle").textContent = current.name;
      }

      // the model may have switched the theme via the internal tool 'ui_set_theme'
      await loadTheme();
      // ...or updated the user instructions via 'preferences_update'
      await loadUserInstructions();

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
  // Chat details (per-chat provider/model)
  // ---------------------------------------------------------------------------

  function fillDetailsModels(providerName) {
    const provider = findProvider(providerName);
    const list = el("lstDetailsModels");
    list.innerHTML = "";
    const models = (provider && Array.isArray(provider.knownModels)) ? provider.knownModels : [];
    models.forEach((m) => {
      const option = document.createElement("option");
      option.value = m;
      list.appendChild(option);
    });
    el("txtDetailsModel").placeholder = (provider && provider.defaultModel) ? provider.defaultModel : "(default)";
  }

  function updateDetailsProviderHint() {
    const changed = state.details && el("selDetailsProvider").value !== state.details.providerName;
    el("detailsProviderHint").hidden = !changed;
  }

  async function openDetails() {
    if (!state.chatId) {
      return;
    }
    try {
      const details = await call(backendUrl, "GetChatDetails", { chatId: state.chatId });
      state.details = details || {};

      const select = el("selDetailsProvider");
      select.innerHTML = "";
      state.providers.forEach((p) => {
        const option = document.createElement("option");
        option.value = p.name;
        option.textContent = p.displayName || p.name;
        select.appendChild(option);
      });
      if (state.details.providerName && !findProvider(state.details.providerName)) {
        const option = document.createElement("option");
        option.value = state.details.providerName;
        option.textContent = state.details.providerName + " (not available)";
        select.appendChild(option);
      }
      select.value = state.details.providerName || "";
      fillDetailsModels(select.value);
      el("txtDetailsModel").value = state.details.model || "";
      el("txtDetailsName").value = state.details.name || el("chatTitle").textContent;

      const info = [];
      info.push("Messages: " + (state.details.messageCount || 0));
      if (state.details.lastAction) {
        info.push("Last action: " + new Date(state.details.lastAction).toLocaleString());
      }
      el("detailsInfo").textContent = info.join(" | ");
      el("detailsContextState").textContent = "(" + (state.details.isCompressed ? "compressed (baseline)" : "full history") + ")";

      let auditLog = [];
      try {
        auditLog = (await call(backendUrl, "GetToolAuditLog", { chatId: state.chatId })) || [];
      } catch (e) {
        // older servers without audit log: the tab stays empty (convention credo)
        auditLog = [];
      }
      state.detailsAuditLog = Array.isArray(auditLog) ? auditLog : [];

      renderChatSources("detailsSkillSources", "detailsSkillSourcesSection", state.details.skillSources, false);
      renderChatSources("detailsToolSources", "detailsToolSourcesSection", state.details.toolSources, true);
      el("detailsSecurityEmpty").hidden = !(el("detailsSkillSourcesSection").hidden && el("detailsToolSourcesSection").hidden);
      updateSkillLevelWarning("detailsSkillSources", "detailsLevelWarning");

      fillAuditFilter();
      renderAuditLog();

      updateDetailsProviderHint();
      selectDetailsTab("detailsTabGeneral");
      el("detailsDialog").hidden = false;
    } catch (e) {
      showError(e);
    }
  }

  function closeDetails() {
    el("detailsDialog").hidden = true;
    state.details = null;
  }

  function selectDetailsTab(tabId) {
    document.querySelectorAll("#detailsDialog .tab").forEach((button) => {
      button.classList.toggle("active", button.dataset.tab === tabId);
    });
    document.querySelectorAll("#detailsDialog .tab-page").forEach((page) => {
      page.hidden = (page.id !== tabId);
    });
  }

  // CHD-3: compresses the context directly from the details dialog
  async function compressFromDetails() {
    closeDetails();
    await compressChat();
  }

  async function saveDetails() {
    try {
      const providerName = el("selDetailsProvider").value;
      const model = el("txtDetailsModel").value.trim();
      const details = state.details || {};
      const newName = el("txtDetailsName").value.trim();
      if (newName.length > 0 && newName !== (details.name || "")) {
        await call(backendUrl, "RenameChat", { chatId: state.chatId, newName: newName });
        el("chatTitle").textContent = newName;
      }
      if (providerName !== details.providerName || model !== (details.model || "")) {
        await call(backendUrl, "SetChatProvider", { chatId: state.chatId, providerName: providerName, model: model });
      }
      const changedSources = collectChangedChatSources(details);
      for (let i = 0; i < changedSources.length; i++) {
        const change = changedSources[i];
        const methodName = change.isTool ? "SetChatToolSourceLevel" : "RaiseChatSkillSourceLevel";
        await call(backendUrl, methodName, { chatId: state.chatId, sourceId: change.sourceId, level: change.level });
      }
      closeDetails();
      // control messages (3.8) are shown immediately; the chat list shows a changed name
      await openChat(state.chatId, el("chatTitle").textContent);
    } catch (e) {
      alert(e.message || e);
    }
  }

  // Renders the per-chat source levels. Skill levels can only be raised (one-way), fixed sources are read-only.
  // Older servers without source information leave the section hidden.
  function renderChatSources(containerId, sectionId, sources, isTool) {
    const container = el(containerId);
    container.innerHTML = "";
    const list = Array.isArray(sources) ? sources : [];
    el(sectionId).hidden = (list.length === 0);

    const levels = isTool ? toolLevels : skillLevels;
    const names = isTool ? toolLevelNames : skillLevelNames;

    list.forEach((s) => {
      const row = document.createElement("div");
      row.className = "source-row";

      const title = document.createElement("span");
      title.className = "source-title";
      title.textContent = s.displayName || s.sourceId;
      const items = Array.isArray(s.itemNames) ? s.itemNames : [];
      title.title = s.sourceId + (items.length > 0 ? ": " + items.join(", ") : "");
      row.appendChild(title);

      const select = document.createElement("select");
      select.className = "level-select";
      select.dataset.sourceId = s.sourceId;
      select.dataset.label = s.displayName || s.sourceId;
      select.dataset.isTool = isTool ? "1" : "0";
      select.dataset.original = s.effectiveLevel || "";
      const minRank = isTool ? 0 : levels.indexOf(s.effectiveLevel);
      levels.forEach((level, rank) => {
        if (rank < minRank) {
          return;
        }
        const option = document.createElement("option");
        option.value = level;
        option.textContent = names[level] + (level === s.defaultLevel ? " (default)" : "");
        select.appendChild(option);
      });
      select.value = s.effectiveLevel || "";
      select.disabled = !!s.isFixed;
      if (!isTool) {
        select.addEventListener("change", () => updateSkillLevelWarning(containerId, "detailsLevelWarning"));
      }
      row.appendChild(select);

      if (isTool) {
        // CHD-8: grey until a tool of the source was used in this chat, then blue; opens the filtered audit log
        const used = (state.detailsAuditLog || []).some((e) => e.toolSourceId === s.sourceId);
        const log = document.createElement("button");
        log.type = "button";
        log.className = "small-button audit-icon" + (used ? " used" : "");
        log.textContent = "\u2630";
        log.title = used ? "Show the tool calls of this source" : "No tool of this source was used in this chat yet";
        log.addEventListener("click", () => {
          el("selDetailsAuditFilter").value = s.sourceId;
          renderAuditLog();
          selectDetailsTab("detailsTabAudit");
        });
        row.appendChild(log);
      }

      if (isTool && s.isApprovedForChat) {
        const badge = document.createElement("span");
        badge.className = "source-badge";
        badge.textContent = "allowed for this chat ";
        const revoke = document.createElement("button");
        revoke.type = "button";
        revoke.className = "small-button";
        revoke.textContent = "Revoke";
        revoke.title = "Ask again before the next call of this tool source";
        revoke.addEventListener("click", async () => {
          try {
            await call(backendUrl, "SetChatToolSourceLevel", { chatId: state.chatId, sourceId: s.sourceId, level: s.effectiveLevel });
            badge.remove();
          } catch (e) {
            alert(e.message || e);
          }
        });
        badge.appendChild(revoke);
        row.appendChild(badge);
      }

      container.appendChild(row);
    });
  }

  // Shows the SKM-6 warning as soon as a skill source is (or is about to be) set to 'Initialize at start'.
  function updateSkillLevelWarning(containerId, warningId) {
    const names = [];
    el(containerId).querySelectorAll("select[data-source-id], select[data-skill-source]").forEach((select) => {
      if (select.value === "InitializeAtStart" && select.dataset.isTool !== "1") {
        names.push(select.dataset.label || select.dataset.sourceId || select.dataset.skillSource);
      }
    });
    const warning = el(warningId);
    warning.hidden = (names.length === 0);
    warning.textContent = "\u26A0 'Initialize at start' loads all skills of the source into every context " +
      "(higher token usage, also for small talk): " + names.join(", ");
  }

  function fillAuditFilter() {
    const select = el("selDetailsAuditFilter");
    select.innerHTML = "";
    const all = document.createElement("option");
    all.value = "";
    all.textContent = "All tool sources";
    select.appendChild(all);
    const sources = (state.details && Array.isArray(state.details.toolSources)) ? state.details.toolSources : [];
    sources.forEach((s) => {
      const option = document.createElement("option");
      option.value = s.sourceId;
      option.textContent = s.displayName || s.sourceId;
      select.appendChild(option);
    });
    select.value = "";
  }

  // Renders the tool audit log of the chat (most recent first), optionally filtered by tool source (CHD-8).
  function renderAuditLog() {
    const container = el("detailsAuditLog");
    container.innerHTML = "";
    const filter = el("selDetailsAuditFilter").value;
    const all = state.detailsAuditLog || [];
    const list = all.filter((e) => !filter || e.toolSourceId === filter).slice().reverse();
    el("detailsAuditEmpty").hidden = (list.length > 0);
    el("detailsAuditState").textContent = all.length > 0 ? "(" + all.length + ")" : "";

    list.forEach((e) => {
      const row = document.createElement("div");
      row.className = "audit-row" + (e.success ? "" : " failed");
      const time = e.timestampUtc ? new Date(e.timestampUtc).toLocaleString() : "";
      const outcome = e.success
        ? ("ok, " + (e.resultSize || 0) + " chars, " + (e.durationMs || 0) + " ms")
        : ("failed: " + (e.errorMessage || ""));
      row.innerHTML =
        "<div><span class=\"muted small\">" + escapeHtml(time) + "</span> <code>" + escapeHtml(e.toolName || "?") + "</code>" +
        (e.toolSourceId ? " <span class=\"muted small\">(" + escapeHtml(e.toolSourceId) + ")</span>" : "") +
        " <span class=\"audit-decision\">" + escapeHtml(e.decision || "") + "</span></div>" +
        "<div class=\"muted small\">" + escapeHtml(e.argumentSummary || "") + "</div>" +
        "<div class=\"small\">" + escapeHtml(outcome) + "</div>";
      container.appendChild(row);
    });
  }

  function collectChangedChatSources() {
    const result = [];
    ["detailsSkillSources", "detailsToolSources"].forEach((containerId) => {
      el(containerId).querySelectorAll("select[data-source-id]").forEach((select) => {
        if (!select.disabled && select.value !== select.dataset.original) {
          result.push({ sourceId: select.dataset.sourceId, level: select.value, isTool: select.dataset.isTool === "1" });
        }
      });
    });
    return result;
  }

  // ---------------------------------------------------------------------------
  // Skill sources (global, via 'IPromptingExtensionService' - optional on the server)
  // ---------------------------------------------------------------------------

  async function loadSkillSources() {
    try {
      state.skillSources = (await call(extensionsUrl, "GetSkillSources", {})) || [];
      state.extensionsAvailable = true;
    } catch (e) {
      // the host does not provide the extension service: hide the feature
      state.skillSources = [];
      state.extensionsAvailable = false;
    }
    renderSkillSourcesState();
  }

  function renderSkillSourcesState() {
    el("skillSourcesUnavailable").hidden = state.extensionsAvailable;
    if (!state.extensionsAvailable) {
      el("skillSourcesState").textContent = "(n/a)";
      return;
    }
    const broken = state.skillSources.filter((s) => s.isReferenceBroken).length;
    let text = "(" + state.skillSources.length;
    if (broken > 0) {
      text += ", " + broken + " \u26A0";
    }
    el("skillSourcesState").textContent = text + ")";
  }

  function renderSkillSourceList() {
    const container = el("skillSourceList");
    container.innerHTML = "";

    state.skillSources.forEach((s) => {
      const row = document.createElement("div");
      row.className = "source-row" + (s.isReferenceBroken ? " broken" : "");

      const title = document.createElement("span");
      title.className = "source-title";
      const skills = Array.isArray(s.skillNames) ? s.skillNames : [];
      title.textContent = (s.isReferenceBroken ? "\u26A0 " : "") + (s.displayName || s.sourceId);
      title.title = (s.kind ? "[" + s.kind + "] " : "") + (s.location || "") +
        (skills.length > 0 ? "\nSkills: " + skills.join(", ") : "") + (s.lastError ? "\n" + s.lastError : "");
      row.appendChild(title);

      const meta = document.createElement("span");
      meta.className = "source-meta muted";
      meta.textContent = (s.kind || "?") + " \u00B7 " + skills.length;
      meta.title = skills.join(", ");
      row.appendChild(meta);

      const select = document.createElement("select");
      select.className = "level-select";
      select.dataset.skillSource = s.sourceId;
      select.dataset.label = s.displayName || s.sourceId;
      skillLevels.forEach((level) => {
        const option = document.createElement("option");
        option.value = level;
        option.textContent = skillLevelNames[level];
        select.appendChild(option);
      });
      select.value = s.level || "OnDemand";
      select.disabled = !!s.isFixed;
      select.addEventListener("change", () => {
        // SKM-6: the warning is shown immediately (before the server round trip)
        updateSkillLevelWarning("skillSourceList", "skillLevelWarning");
        runSkillSourceAction("SetSkillSourceLevel", { sourceId: s.sourceId, level: select.value });
      });
      row.appendChild(select);

      if (!s.isFixed) {
        const rename = document.createElement("button");
        rename.type = "button";
        rename.className = "small-button";
        rename.textContent = "\u270E";
        rename.title = "Change the label of this source (empty = default label)";
        rename.addEventListener("click", () => {
          const value = prompt("Label of the skill source (empty = default):", s.displayName || "");
          if (value !== null) {
            runSkillSourceAction("SetSkillSourceDisplayName", { sourceId: s.sourceId, displayName: value });
          }
        });
        row.appendChild(rename);
      }

      const secure = document.createElement("label");
      secure.className = "source-secure";
      const secureCheckbox = document.createElement("input");
      secureCheckbox.type = "checkbox";
      secureCheckbox.checked = !!s.secureAvailability;
      secureCheckbox.disabled = !!s.isFixed || !s.supportsSecureAvailability;
      secureCheckbox.addEventListener("change", () => runSkillSourceAction(
        "SetSkillSourceSecureAvailability", { sourceId: s.sourceId, enabled: secureCheckbox.checked }
      ));
      secure.appendChild(secureCheckbox);
      secure.appendChild(document.createTextNode(" Offline"));
      secure.title = "Secure availability: keeps a local offline copy, which is refreshed once on every new chat start" +
        (s.offlineCopyTimestampUtc ? " (last copy: " + new Date(s.offlineCopyTimestampUtc).toLocaleString() + ")" : "");
      row.appendChild(secure);

      if (s.kind === "Url" || s.kind === "GitHub") {
        const credential = document.createElement("button");
        credential.type = "button";
        credential.className = "small-button";
        credential.textContent = s.hasCredentials ? "Token \u2713" : "Token";
        credential.title = "Sets, replaces or (with an empty value) removes the PAT/token of this source" +
          (s.hasCredentials ? " (" + (s.credentialHint || "") + ")" : "");
        credential.addEventListener("click", () => {
          const value = prompt("PAT/token for '" + (s.displayName || s.sourceId) + "' (empty = remove):", "");
          if (value !== null) {
            runSkillSourceAction("SetSkillSourceCredential", { sourceId: s.sourceId, credential: value });
          }
        });
        row.appendChild(credential);
      }

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "small-button";
      remove.textContent = "\u2716";
      remove.title = "Remove this skill source";
      remove.disabled = !!s.isFixed;
      remove.addEventListener("click", () => {
        if (confirm("Remove the skill source '" + (s.displayName || s.sourceId) + "'? (referenced files are not deleted)")) {
          runSkillSourceAction("RemoveSkillSource", { sourceId: s.sourceId });
        }
      });
      row.appendChild(remove);

      container.appendChild(row);
    });

    updateSkillLevelWarning("skillSourceList", "skillLevelWarning");
  }

  async function runSkillSourceAction(methodName, args) {
    try {
      await call(extensionsUrl, methodName, args);
    } catch (e) {
      alert(e.message || e);
    }
    await loadSkillSources();
    renderSkillSourceList();
  }

  async function openSkillSourcesTab() {
    await loadSkillSources();
    renderSkillSourceList();
    try {
      el("txtSystemPreferencesLocation").value = (await call(extensionsUrl, "GetSystemPreferencesLocation", {})) || "";
    } catch (e) {
      el("txtSystemPreferencesLocation").value = "";
    }
  }

  // ---------------------------------------------------------------------------
  // Settings tabs
  // ---------------------------------------------------------------------------

  function selectSettingsTab(tabId) {
    document.querySelectorAll("#settingsDialog .tab").forEach((button) => {
      button.classList.toggle("active", button.dataset.tab === tabId);
    });
    document.querySelectorAll("#settingsDialog .tab-page").forEach((page) => {
      page.hidden = (page.id !== tabId);
    });
    // provider specific buttons only make sense on the general tab
    el("btnDeleteKey").hidden = (tabId !== "settingsTabGeneral");
    if (tabId === "settingsTabSkills") {
      openSkillSourcesTab();
    } else if (tabId === "settingsTabTools") {
      openToolSourcesTab();
    }
  }

  // ---------------------------------------------------------------------------
  // Tool sources (global, via 'IPromptingExtensionService' - optional on the server)
  // ---------------------------------------------------------------------------

  async function loadToolSources() {
    try {
      state.toolSources = (await call(extensionsUrl, "GetToolSources", {})) || [];
      state.toolSourcesAvailable = true;
    } catch (e) {
      state.toolSources = [];
      state.toolSourcesAvailable = false;
    }
    renderToolSourcesState();
  }

  function renderToolSourcesState() {
    el("toolSourcesUnavailable").hidden = state.toolSourcesAvailable;
    if (!state.toolSourcesAvailable) {
      el("toolSourcesState").textContent = "(n/a)";
      return;
    }
    const broken = state.toolSources.filter((s) => s.isReferenceBroken).length;
    let text = "(" + state.toolSources.length;
    if (broken > 0) {
      text += ", " + broken + " \u26A0";
    }
    el("toolSourcesState").textContent = text + ")";
  }

  function renderToolSourceList() {
    const container = el("toolSourceList");
    container.innerHTML = "";

    state.toolSources.forEach((s) => {
      const row = document.createElement("div");
      row.className = "source-row" + (s.isReferenceBroken ? " broken" : "");

      const title = document.createElement("span");
      title.className = "source-title";
      const tools = Array.isArray(s.toolNames) ? s.toolNames : [];
      title.textContent = (s.isReferenceBroken ? "\u26A0 " : "") + (s.displayName || s.sourceId);
      title.title = (s.kind ? "[" + s.kind + "] " : "") + (s.location ? s.location + "\n" : "") + tools.join(", ") + (s.brokenReason ? "\n" + s.brokenReason : "");
      row.appendChild(title);

      const meta = document.createElement("span");
      meta.className = "source-meta muted";
      meta.textContent = (s.kind || "?") + " \u00B7 " + tools.length;
      meta.title = tools.join(", ");
      row.appendChild(meta);

      const select = document.createElement("select");
      select.className = "level-select";
      toolLevels.forEach((level) => {
        const option = document.createElement("option");
        option.value = level;
        option.textContent = toolLevelNames[level] + (level === s.defaultLevel ? " (default)" : "");
        select.appendChild(option);
      });
      select.value = s.level || "Hidden";
      select.disabled = !!s.isFixed;
      select.addEventListener("change", () => runToolSourceAction("SetToolSourceLevel", { sourceId: s.sourceId, level: select.value }));
      row.appendChild(select);

      if (s.supportsCredential) {
        const credential = document.createElement("button");
        credential.type = "button";
        credential.className = "small-button";
        credential.textContent = s.hasCredentials ? "Token \u2713" : "Token";
        credential.title = "Set or remove the token of this source";
        credential.addEventListener("click", () => {
          const value = prompt("Token for '" + (s.displayName || s.sourceId) + "' (empty = remove):", "");
          if (value !== null) {
            runToolSourceAction("SetToolSourceCredential", { sourceId: s.sourceId, credential: value });
          }
        });
        row.appendChild(credential);
      }

      if (s.hasSettings) {
        const settings = document.createElement("button");
        settings.type = "button";
        settings.className = "small-button";
        settings.textContent = "Settings";
        settings.addEventListener("click", () => openToolSettings(s));
        row.appendChild(settings);
      }

      if (!s.isFixed) {
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "small-button";
        remove.textContent = "\u2716";
        remove.title = "Remove this tool source";
        remove.addEventListener("click", () => {
          if (confirm("Remove the tool source '" + (s.displayName || s.sourceId) + "'?")) {
            runToolSourceAction("RemoveToolSource", { sourceId: s.sourceId });
          }
        });
        row.appendChild(remove);
      }

      container.appendChild(row);
    });

    if (state.toolSourcesAvailable && state.toolSources.length === 0) {
      container.textContent = "(no tool sources)";
    }
  }

  async function runToolSourceAction(methodName, args) {
    try {
      await call(extensionsUrl, methodName, args);
    } catch (e) {
      alert(e.message || e);
    }
    await loadToolSources();
    renderToolSourceList();
  }

  async function openToolSourcesTab() {
    el("toolSettingsEditor").hidden = true;
    await loadToolSources();
    renderToolSourceList();
  }

  async function openToolSettings(source) {
    try {
      const json = await call(extensionsUrl, "GetToolProviderSettings", { sourceId: source.sourceId });
      let pretty = json || "{}";
      let parsed = null;
      try {
        parsed = JSON.parse(pretty);
        pretty = JSON.stringify(parsed, null, 2);
      } catch (e) {
        // keep the raw text
        parsed = null;
      }
      el("toolSettingsTitle").textContent = source.displayName || source.sourceId;
      el("txtToolSettings").value = pretty;
      el("toolSettingsEditor").dataset.sourceId = source.sourceId;

      const hasAllowLists = !!parsed && typeof parsed === "object" && !Array.isArray(parsed) &&
        allowListDefinitions.some((d) => Array.isArray(parsed[d.key]));
      el("toolSettingsRawToggle").hidden = !hasAllowLists;
      el("chkToolSettingsRaw").checked = !hasAllowLists;
      if (hasAllowLists) {
        renderAllowListEditor(parsed);
      }
      applyToolSettingsMode();

      el("toolSettingsEditor").hidden = false;
    } catch (e) {
      alert(e.message || e);
    }
  }

  // ---------------------------------------------------------------------------
  // Allow-list editors (fixed editors for the guards of the built-in tool providers, UIC-1 fallback)
  // Unknown properties of the settings object are preserved untouched.
  // ---------------------------------------------------------------------------

  const allowListDefinitions = [
    { key: "allowedPaths", title: "Allowed paths", valueKey: "path", optionKey: "access", options: ["RO", "RW"], placeholder: "C:\\data or \\\\server\\share" },
    { key: "allowedDomains", title: "Allowed domains", valueKey: null, optionKey: null, options: null, placeholder: "example.com or *.example.com" },
    { key: "allowedCommands", title: "Allowed shell commands", valueKey: "pattern", optionKey: "shell", options: ["cmd", "powershell"], placeholder: "git status, dir *, regex:^git (log|diff)" }
  ];

  function renderAllowListEditor(settings) {
    const container = el("toolAllowListEditor");
    container.innerHTML = "";
    container.settingsObject = settings;

    allowListDefinitions.forEach((definition) => {
      if (!Array.isArray(settings[definition.key])) {
        return;
      }

      const section = document.createElement("div");
      section.className = "allow-list";
      section.dataset.key = definition.key;

      const title = document.createElement("div");
      title.className = "capabilities-title";
      title.textContent = definition.title;
      section.appendChild(title);

      const rows = document.createElement("div");
      rows.className = "allow-list-rows";
      section.appendChild(rows);

      settings[definition.key].forEach((entry) => {
        rows.appendChild(createAllowListRow(definition, entry));
      });

      const add = document.createElement("button");
      add.type = "button";
      add.className = "small-button";
      add.textContent = "+ Add";
      add.addEventListener("click", () => {
        const row = createAllowListRow(definition, null);
        rows.appendChild(row);
        row.querySelector("input").focus();
      });
      section.appendChild(add);

      container.appendChild(section);
    });
  }

  function createAllowListRow(definition, entry) {
    const row = document.createElement("div");
    row.className = "source-add-row allow-list-row";

    let value = "";
    let option = definition.options ? definition.options[0] : null;
    if (typeof entry === "string") {
      value = entry;
    } else if (entry && typeof entry === "object" && definition.valueKey) {
      value = entry[definition.valueKey] || "";
      if (entry[definition.optionKey]) {
        option = entry[definition.optionKey];
      }
    }

    const input = document.createElement("input");
    input.type = "text";
    input.value = value;
    input.placeholder = definition.placeholder;
    row.appendChild(input);

    if (definition.options) {
      const select = document.createElement("select");
      select.className = "level-select";
      definition.options.forEach((o) => {
        const item = document.createElement("option");
        item.value = o;
        item.textContent = o;
        select.appendChild(item);
      });
      // unknown values are kept (convention credo)
      if (definition.options.indexOf(option) < 0) {
        const item = document.createElement("option");
        item.value = option;
        item.textContent = option;
        select.appendChild(item);
      }
      select.value = option;
      row.appendChild(select);
    }

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "small-button";
    remove.textContent = "\u2716";
    remove.title = "Remove this entry";
    remove.addEventListener("click", () => row.remove());
    row.appendChild(remove);

    return row;
  }

  function collectAllowListSettings() {
    const container = el("toolAllowListEditor");
    const settings = Object.assign({}, container.settingsObject || {});

    container.querySelectorAll(".allow-list").forEach((section) => {
      const definition = allowListDefinitions.find((d) => d.key === section.dataset.key);
      const entries = [];
      section.querySelectorAll(".allow-list-row").forEach((row) => {
        const value = row.querySelector("input").value.trim();
        if (value.length === 0) {
          return;
        }
        if (!definition.valueKey) {
          entries.push(value);
          return;
        }
        const entry = {};
        entry[definition.valueKey] = value;
        entry[definition.optionKey] = row.querySelector("select").value;
        entries.push(entry);
      });
      settings[definition.key] = entries;
    });

    return settings;
  }

  // switches between the fixed allow-list editors and the raw JSON editor (keeping both in sync)
  function applyToolSettingsMode() {
    const raw = el("chkToolSettingsRaw").checked || el("toolSettingsRawToggle").hidden;
    el("toolAllowListEditor").hidden = raw;
    el("txtToolSettings").hidden = !raw;
  }

  function toggleToolSettingsMode() {
    if (el("chkToolSettingsRaw").checked) {
      el("txtToolSettings").value = JSON.stringify(collectAllowListSettings(), null, 2);
    } else {
      try {
        renderAllowListEditor(JSON.parse(el("txtToolSettings").value || "{}"));
      } catch (e) {
        alert("The JSON is invalid: " + (e.message || e));
        el("chkToolSettingsRaw").checked = true;
      }
    }
    applyToolSettingsMode();
  }

  async function saveToolSettings() {
    const sourceId = el("toolSettingsEditor").dataset.sourceId;
    try {
      let settingsJson = el("txtToolSettings").value;
      if (!el("toolAllowListEditor").hidden) {
        settingsJson = JSON.stringify(collectAllowListSettings());
      }
      await call(extensionsUrl, "SaveToolProviderSettings", { sourceId: sourceId, settingsJson: settingsJson });
      el("toolSettingsEditor").hidden = true;
      await loadToolSources();
      renderToolSourceList();
    } catch (e) {
      alert(e.message || e);
    }
  }

  async function addMcpServer() {
    const url = el("txtMcpUrl").value.trim();
    if (url.length === 0) {
      return;
    }
    await runToolSourceAction("AddMcpServer", { url: url, token: el("txtMcpToken").value.trim() || null });
    el("txtMcpUrl").value = "";
    el("txtMcpToken").value = "";
  }

  function readFileAsBase64(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        const text = String(reader.result || "");
        const commaIndex = text.indexOf(",");
        resolve(commaIndex >= 0 ? text.substring(commaIndex + 1) : text);
      };
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(file);
    });
  }

  async function uploadToolAssembly() {
    const input = el("fileToolAssembly");
    if (!input.files || input.files.length === 0) {
      return;
    }
    try {
      const file = input.files[0];
      const content = await readFileAsBase64(file);
      const interfaces = (await call(extensionsUrl, "UploadToolAssembly", { fileName: file.name, contentBase64: content })) || [];
      const select = el("selContractInterface");
      select.innerHTML = "";
      interfaces.forEach((i) => {
        const option = document.createElement("option");
        option.value = i.interfaceName;
        option.dataset.assemblyId = i.assemblyId;
        const methods = Array.isArray(i.methodNames) ? i.methodNames : [];
        option.textContent = i.interfaceName + " (" + methods.length + " methods)";
        select.appendChild(option);
      });
      el("contractRegisterRow").hidden = (interfaces.length === 0);
      if (interfaces.length === 0) {
        alert("The assembly does not contain any public interface.");
      }
    } catch (e) {
      alert(e.message || e);
    }
  }

  async function registerContractInterface() {
    const select = el("selContractInterface");
    const option = select.options[select.selectedIndex];
    const url = el("txtContractUrl").value.trim();
    if (!option || url.length === 0) {
      return;
    }
    await runToolSourceAction("RegisterContractInterface", {
      assemblyId: option.dataset.assemblyId, interfaceName: option.value, url: url, token: el("txtContractToken").value.trim() || null
    });
    el("contractRegisterRow").hidden = true;
    el("txtContractUrl").value = "";
    el("txtContractToken").value = "";
    el("fileToolAssembly").value = "";
  }

  async function addSkillSource() {
    const location = el("txtSkillSourceLocation").value.trim();
    if (location.length === 0) {
      return;
    }
    const kind = el("selSkillSourceKind").value;
    let credential = null;
    if (isRemoteSkillSourceKind(kind)) {
      credential = el("txtSkillSourceCredential").value.trim() || null;
    }
    await runSkillSourceAction("AddSkillSource", { kind: kind, location: location, credential: credential });
    el("txtSkillSourceLocation").value = "";
    el("txtSkillSourceCredential").value = "";
  }

  function isRemoteSkillSourceKind(kind) {
    return kind === "Url" || kind === "GitHub";
  }

  function updateSkillSourceAddRow() {
    const kind = el("selSkillSourceKind").value;
    const placeholders = {
      File: "Local or UNC path (for example C:\\skills\\my-skill\\SKILL.md)",
      Folder: "Local or UNC folder (a skill folder or a folder of skill folders)",
      Url: "https://.../SKILL.md",
      GitHub: "https://github.com/{owner}/{repo}[/tree/{ref}/{path}]"
    };
    el("txtSkillSourceLocation").placeholder = placeholders[kind] || "";
    el("txtSkillSourceCredential").hidden = !isRemoteSkillSourceKind(kind);
  }

  async function setSystemPreferencesLocation() {
    await runSkillSourceAction("SetSystemPreferencesLocation", { location: el("txtSystemPreferencesLocation").value.trim() });
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

    el("selTheme").value = state.theme;
    renderUserInstructionsState();
    loadUserInstructions();
    renderSkillSourcesState();
    renderToolSourcesState();
    selectSettingsTab("settingsTabGeneral");

    el("settingsDialog").hidden = false;
    el("txtApiKey").focus();
  }

  function closeSettings() {
    el("settingsDialog").hidden = true;
    el("txtApiKey").value = "";
    // revert a theme preview which was not saved
    document.body.classList.toggle("dark", state.theme === "dark");
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
      const theme = el("selTheme").value;
      await call(settingsUrl, "SaveUiTheme", { themeName: theme });
      applyTheme(theme);
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
  // Theme (light/dark) - persisted via the settings service, also switchable by the model ('ui_set_theme')
  // ---------------------------------------------------------------------------

  function applyTheme(theme) {
    state.theme = (theme === "dark") ? "dark" : "light";
    document.body.classList.toggle("dark", state.theme === "dark");
    try {
      localStorage.setItem("aiChatTheme", state.theme);
    } catch (e) {
      // storage not available: the theme is still applied
    }
  }

  async function loadTheme() {
    try {
      applyTheme(await call(settingsUrl, "GetUiTheme", {}));
    } catch (e) {
      // older servers do not know the operation: keep the current theme
    }
  }

  // ---------------------------------------------------------------------------
  // User instructions (provided to the model as mandatory skill, for example 'global-user-preferences')
  // ---------------------------------------------------------------------------

  async function loadUserInstructions() {
    try {
      state.userInstructions = (await call(settingsUrl, "GetUserInstructions", {})) || "";
    } catch (e) {
      state.userInstructions = "";
    }
    renderUserInstructionsState();
  }

  function renderUserInstructionsState() {
    const text = state.userInstructions || "";
    if (text.trim().length === 0) {
      el("userInstructionsState").textContent = "(none)";
    } else {
      el("userInstructionsState").textContent = "(" + text.length + " characters)";
    }
  }

  async function openUserInstructions() {
    // always read the current state (a tool may have changed the file in the meantime)
    await loadUserInstructions();
    el("txtUserInstructions").value = state.userInstructions || "";
    el("userInstructionsDialog").hidden = false;
    el("txtUserInstructions").focus();
  }

  function closeUserInstructions() {
    el("userInstructionsDialog").hidden = true;
  }

  async function saveUserInstructions() {
    try {
      await call(settingsUrl, "SaveUserInstructions", { userInstructions: el("txtUserInstructions").value });
      closeUserInstructions();
      await loadUserInstructions();
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
    el("btnDetails").addEventListener("click", openDetails);
    el("btnDetailsCancel").addEventListener("click", closeDetails);
    el("btnDetailsSave").addEventListener("click", saveDetails);
    el("selDetailsProvider").addEventListener("change", () => {
      fillDetailsModels(el("selDetailsProvider").value);
      el("txtDetailsModel").value = "";
      updateDetailsProviderHint();
    });
    el("btnSettings").addEventListener("click", openSettings);
    el("btnSettingsCancel").addEventListener("click", closeSettings);
    el("btnSettingsSave").addEventListener("click", saveSettings);
    el("btnDeleteKey").addEventListener("click", deleteApiKey);
    el("selProvider").addEventListener("change", () => fillSettingsDialog(el("selProvider").value));
    el("selTheme").addEventListener("change", () => document.body.classList.toggle("dark", el("selTheme").value === "dark"));
    el("btnUserInstructions").addEventListener("click", openUserInstructions);
    el("btnUserInstructionsCancel").addEventListener("click", closeUserInstructions);
    el("btnUserInstructionsSave").addEventListener("click", saveUserInstructions);
    document.querySelectorAll("#settingsDialog .tab").forEach((button) => {
      button.addEventListener("click", () => selectSettingsTab(button.dataset.tab));
    });
    el("btnSkillSourceAdd").addEventListener("click", addSkillSource);
    el("selSkillSourceKind").addEventListener("change", updateSkillSourceAddRow);
    el("btnSystemPreferencesSet").addEventListener("click", setSystemPreferencesLocation);
    el("btnMcpAdd").addEventListener("click", addMcpServer);
    el("btnToolAssemblyUpload").addEventListener("click", uploadToolAssembly);
    el("btnContractRegister").addEventListener("click", registerContractInterface);
    el("btnToolSettingsSave").addEventListener("click", saveToolSettings);
    el("chkToolSettingsRaw").addEventListener("change", toggleToolSettingsMode);
    document.querySelectorAll("#detailsDialog .tab").forEach((button) => {
      button.addEventListener("click", () => selectDetailsTab(button.dataset.tab));
    });
    el("btnDetailsCompress").addEventListener("click", compressFromDetails);
    el("selDetailsAuditFilter").addEventListener("change", () => renderAuditLog());
    el("btnToolSettingsCancel").addEventListener("click", () => {
      el("toolSettingsEditor").hidden = true;
    });

    el("txtMessage").addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
      }
    });
  }

  async function init() {
    // apply the last known theme immediately (avoids flickering), the server value follows
    try {
      applyTheme(localStorage.getItem("aiChatTheme"));
    } catch (e) {
      applyTheme("light");
    }
    wire();
    updateHeaderButtons();
    try {
      await loadTheme();
      await loadSettings();
      await loadUserInstructions();
      await loadSkillSources();
      await loadToolSources();
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
