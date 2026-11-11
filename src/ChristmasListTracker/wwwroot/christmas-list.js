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
    }
};
