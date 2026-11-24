document.addEventListener("DOMContentLoaded", function () {
    const root = document.getElementById("libraryChat");
    const panel = document.getElementById("chatPanel");
    const toggle = document.getElementById("chatToggle");
    const close = document.getElementById("chatClose");
    const send = document.getElementById("chatSend");
    const input = document.getElementById("chatInput");
    const messages = document.getElementById("chatMessages");
    const suggestionsEl = document.getElementById("chatSuggestions");

    if (!root || !panel || !toggle || !send || !input || !messages) {
        return;
    }

    function readBootstrapConfig() {
        const el = document.getElementById("chatBootstrapConfig");
        if (el && el.textContent) {
            try {
                return JSON.parse(el.textContent);
            } catch (e) {
                console.warn("ChatBot: invalid bootstrap config", e);
            }
        }
        return {
            role: root.dataset.chatRole || "",
            area: root.dataset.chatArea || "",
            controller: root.dataset.chatController || "",
            action: root.dataset.chatAction || "",
            pageKey: root.dataset.chatPageKey || "",
            pageName: root.dataset.chatPageName || "",
            pageContext: root.dataset.chatPageContext || "",
            isMultipurpose: root.dataset.chatMultipurpose === "true",
            requiresAuth: root.dataset.chatRequiresAuth === "true",
            greeting: "Xin chào! Tôi là trợ lý AI của LibraryHub. Tôi có thể giúp gì cho bạn hôm nay?",
            placeholder: "Nhập câu hỏi của bạn...",
            suggestions: []
        };
    }

    const config = readBootstrapConfig();
    const requiresAuth = !!config.requiresAuth || String(config.role || "").toLowerCase() === "guest";
    if (config.placeholder) {
        input.placeholder = config.placeholder;
    }

    const token = document.querySelector(
        '#chatTokenForm input[name="__RequestVerificationToken"]'
    )?.value;

    function escapeHtml(text) {
        return String(text || "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    function renderBotMessage(text) {
        let html = escapeHtml(text || "");
        // Markdown links: [label](url) - chỉ cho phép /exports hoặc http(s)
        html = html.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, function (_, label, url) {
            const safeUrl = String(url || "");
            if (/^(https?:\/\/|\/)/i.test(safeUrl)) {
                return '<a href="' + safeUrl + '" target="_blank" rel="noopener noreferrer">' + label + '</a>';
            }
            return label + " (" + safeUrl + ")";
        });
        // Bold **text**
        html = html.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
        // Inline code `text`
        html = html.replace(/`([^`]+)`/g, "<code>$1</code>");
        // Line breaks
        html = html.replace(/\n/g, "<br>");
        return html;
    }

    function scrollToBottom() {
        messages.scrollTop = messages.scrollHeight;
    }

    function addMessage(content, type, options) {
        const element = document.createElement("div");
        element.className = `chat-message ${type}`;
        const opts = options || {};

        if (type === "bot" && opts.formatted) {
            element.innerHTML = renderBotMessage(content);
        } else {
            element.textContent = content;
        }

        if (opts.export && opts.export.url) {
            const wrap = document.createElement("div");
            wrap.className = "chat-export-link";
            const link = document.createElement("a");
            link.href = opts.export.url;
            link.target = "_blank";
            link.rel = "noopener noreferrer";
            link.className = "chat-export-button";
            const fileType = (opts.export.type || "file").toUpperCase();
            const fileName = opts.export.filename || "Tải báo cáo";
            link.textContent = `Tải ${fileType}: ${fileName}`;
            wrap.appendChild(link);
            element.appendChild(document.createElement("br"));
            element.appendChild(wrap);
        }

        messages.appendChild(element);
        scrollToBottom();
        return element;
    }

    function renderSuggestions() {
        if (!suggestionsEl) return;
        suggestionsEl.innerHTML = "";
        const list = Array.isArray(config.suggestions) ? config.suggestions : [];
        if (!list.length) {
            suggestionsEl.hidden = true;
            return;
        }
        suggestionsEl.hidden = false;
        list.forEach(function (text) {
            if (!text) return;
            const btn = document.createElement("button");
            btn.type = "button";
            btn.className = "chat-suggestion-chip";
            btn.textContent = String(text);
            btn.addEventListener("click", function () {
                input.value = String(text);
                input.focus();
                // Tự động gửi để trải nghiệm nhanh trên từng giao diện
                sendMessage();
            });
            suggestionsEl.appendChild(btn);
        });
    }

    function guestReplyMessage() {
        return "Bạn cần đăng nhập để sử dụng đầy đủ trợ lý LibraryHub (tra cứu theo tài khoản, lịch sử mượn/trả, thống kê và các chức năng theo vai trò).\n\n" +
            "- Đăng nhập: /Login\n" +
            "- Đăng ký: /Register\n\n" +
            "Sau khi đăng nhập, bạn có thể mở lại biểu tượng chatbot để được hỗ trợ theo đúng giao diện và vai trò của mình.";
    }

    function ensureGreeting() {
        if (messages.childElementCount > 0) return;
        const greeting = config.greeting || "Xin chào! Tôi là trợ lý AI của LibraryHub. Tôi có thể giúp gì cho bạn hôm nay?";
        addMessage(greeting, "bot", { formatted: false });
    }

    renderSuggestions();
    ensureGreeting();

    function clearPageOverlays() {
        if (!document.body) return;
        if (document.body.classList.contains("mobile-nav-active")) {
            document.body.classList.remove("mobile-nav-active");
            const mobileToggle = document.querySelector(".mobile-nav-toggle");
            if (mobileToggle) {
                mobileToggle.classList.add("bi-list");
                mobileToggle.classList.remove("bi-x");
            }
        }
        if (document.body.classList.contains("toggle-sidebar")) {
            document.body.classList.remove("toggle-sidebar");
        }

        const hasOpenModal = !!document.querySelector(".modal.show, .modal.showing");
        const hasOpenOffcanvas = !!document.querySelector(".offcanvas.show, .offcanvas.showing");

        document.querySelectorAll(".modal-backdrop, .offcanvas-backdrop").forEach(function (el) {
            const isOffcanvasBackdrop = el.classList.contains("offcanvas-backdrop");
            const keepForOpenComponent = (isOffcanvasBackdrop && hasOpenOffcanvas) || (!isOffcanvasBackdrop && hasOpenModal);
            if (!keepForOpenComponent) {
                el.remove();
            }
        });

        if (!hasOpenModal) {
            document.body.classList.remove("modal-open");
            document.body.style.removeProperty("overflow");
            document.body.style.removeProperty("padding-right");
        }
    }

    toggle.addEventListener("click", function () {
        clearPageOverlays();
        panel.hidden = !panel.hidden;
        if (!panel.hidden) {
            clearPageOverlays();
            input.focus();
            scrollToBottom();
        }
    });

    close?.addEventListener("click", function () {
        panel.hidden = true;
    });

    async function sendMessage() {
        const message = input.value.trim();

        if (!message || send.disabled) {
            return;
        }

        addMessage(message, "user");

        input.value = "";
        send.disabled = true;

        if (requiresAuth) {
            addMessage(guestReplyMessage(), "bot", { formatted: true });
            send.disabled = false;
            input.focus();
            scrollToBottom();
            return;
        }

        const loading = addMessage("LibraryHub đang trả lời...", "bot");

        const payload = {
            message: message,
            pageContext: config.pageContext || (window.location ? window.location.href : ""),
            interfaceContext: {
                role: config.role || "",
                area: config.area || "",
                controller: config.controller || "",
                action: config.action || "",
                pageKey: config.pageKey || "",
                pageName: config.pageName || "",
                isMultipurpose: !!config.isMultipurpose,
                url: window.location ? window.location.href : ""
            }
        };

        try {
            const response = await fetch("/chat/send", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "RequestVerificationToken": token
                },
                body: JSON.stringify(payload)
            });

            let result = {};
            try {
                result = await response.json();
            } catch (parseErr) {
                result = { success: false, message: "Phản hồi từ máy chủ không hợp lệ." };
            }

            console.log("ChatBot HTTP Status:", response.status);
            console.log("ChatBot Response:", result);

            const msg = result.message || "Không nhận được câu trả lời.";
            loading.innerHTML = renderBotMessage(msg);

            if (result.export && result.export.url) {
                const wrap = document.createElement("div");
                wrap.className = "chat-export-link";
                const link = document.createElement("a");
                link.href = result.export.url;
                link.target = "_blank";
                link.rel = "noopener noreferrer";
                link.className = "chat-export-button";
                const fileType = (result.export.type || "file").toUpperCase();
                const fileName = result.export.filename || "Tải báo cáo";
                link.textContent = `Tải ${fileType}: ${fileName}`;
                wrap.appendChild(link);
                loading.appendChild(document.createElement("br"));
                loading.appendChild(wrap);
            }

            if ((response.status === 401 || response.status === 403) && !result.message) {
                loading.innerHTML = renderBotMessage("Phiên đăng nhập không hợp lệ hoặc bạn không có quyền sử dụng chatbot. Vui lòng đăng nhập lại.");
            } else if (!response.ok && !result.message) {
                loading.textContent = "Đã xảy ra lỗi. Vui lòng thử lại.";
            }
        }
        catch (error) {
            console.error("LibraryHub ChatBot Error:", error);
            loading.textContent = "Không thể kết nối đến ChatBot. Vui lòng thử lại.";
        }
        finally {
            send.disabled = false;
            input.focus();
            scrollToBottom();
        }
    }

    send.addEventListener("click", sendMessage);

    input.addEventListener("keydown", function (event) {
        if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            sendMessage();
        }
    });
});