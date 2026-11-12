window.christmasList = {
    getUserId: () => {
        const prefix = "christmas-list-user=";
        const entry = document.cookie.split("; ").find(value => value.startsWith(prefix));
        return entry ? decodeURIComponent(entry.slice(prefix.length)) : null;
    },
    setUserId: id => {
        const secure = location.protocol === "https:" ? "; Secure" : "";
        document.cookie = `christmas-list-user=${encodeURIComponent(id)}; Max-Age=31536000; Path=/; SameSite=Lax${secure}`;
    },
    clearUserId: () => {
        const secure = location.protocol === "https:" ? "; Secure" : "";
        document.cookie = `christmas-list-user=; Max-Age=0; Path=/; SameSite=Lax${secure}`;
    },
    uploadFiles: async (input, progress, maxFiles, maxBytes) => {
        const files = Array.from(input.files || []);
        const result = { uploaded: 0, errors: [] };

        if (files.length > maxFiles) {
            return { uploaded: 0, errors: [`Choose no more than ${maxFiles} images at a time.`] };
        }

        const requestJson = async (url, body) => {
            let lastError;
            for (let attempt = 0; attempt < 2; attempt++) {
                try {
                    const response = await fetch(url, {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: body === undefined ? undefined : JSON.stringify(body)
                    });
                    if (!response.ok) {
                        let message = `Request failed (HTTP ${response.status}).`;
                        try {
                            const payload = await response.json();
                            message = payload.error || payload.detail || payload.title || message;
                        } catch {
                        }
                        throw new Error(message);
                    }
                    return response.status === 204 ? null : await response.json().catch(() => null);
                } catch (error) {
                    lastError = error;
                    if (attempt === 1) {
                        throw lastError;
                    }
                }
            }
        };

        const putFile = (url, file, contentType, fileNumber) => new Promise((resolve, reject) => {
            const request = new XMLHttpRequest();
            request.open("PUT", url);
            request.setRequestHeader("Content-Type", contentType);
            request.timeout = 300000;
            request.upload.onprogress = event => {
                if (event.lengthComputable) {
                    const percent = Math.floor((event.loaded / event.total) * 100);
                    progress.invokeMethodAsync("ReportUploadProgressAsync", fileNumber, files.length, percent).catch(() => {});
                }
            };
            request.onload = () => request.status >= 200 && request.status < 300
                ? resolve()
                : reject(new Error(`R2 upload failed (HTTP ${request.status}).`));
            request.onerror = () => reject(new Error("Network error while uploading directly to R2."));
            request.ontimeout = () => reject(new Error("The direct upload to R2 timed out."));
            request.onabort = () => reject(new Error("The direct upload was cancelled."));
            request.send(file);
        });

        for (let index = 0; index < files.length; index++) {
            const file = files[index];
            let itemId = null;
            try {
                const extension = file.name.split(".").pop().toLowerCase();
                const inferredType = extension === "png" ? "image/png" :
                    (extension === "jpg" || extension === "jpeg" ? "image/jpeg" : "");
                const contentType = file.type || inferredType;
                if (!["image/jpeg", "image/png"].includes(contentType)) {
                    throw new Error("Only JPEG and PNG images are supported.");
                }
                if (file.size <= 0 || file.size > maxBytes) {
                    throw new Error("The image must be no larger than 20 MB.");
                }

                const grant = await requestJson("/api/uploads/prepare", {
                    uploadId: crypto.randomUUID(),
                    fileName: file.name,
                    contentType,
                    size: file.size
                });
                itemId = grant.itemId;
                const uploadStarted = performance.now();
                await putFile(grant.url, file, grant.contentType, index + 1);
                const durationMs = performance.now() - uploadStarted;
                await requestJson(`/api/uploads/${itemId}/complete`, { durationMs });
                result.uploaded++;
            } catch (error) {
                if (itemId) {
                    try {
                        await requestJson(`/api/uploads/${itemId}/abort`);
                    } catch {
                    }
                }
                result.errors.push(`${file.name}: ${error.message || "Upload failed."}`);
            }
        }

        input.value = "";
        return result;
    }
};
