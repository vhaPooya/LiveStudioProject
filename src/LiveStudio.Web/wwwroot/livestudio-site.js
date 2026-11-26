// LiveStudio site helpers (auth hand-off, reveal-on-scroll, clipboard, header shadow).
window.lsSite = (() => {
    let observer;
    const initReveal = () => {
        if (!('IntersectionObserver' in window)) { document.querySelectorAll('.reveal').forEach(e => e.classList.add('in')); return; }
        observer ??= new IntersectionObserver(entries => entries.forEach(e => { if (e.isIntersecting) { e.target.classList.add('in'); observer.unobserve(e.target); } }), { threshold: .12 });
        document.querySelectorAll('.reveal:not(.in)').forEach(e => observer.observe(e));
    };
    window.addEventListener('scroll', () => document.querySelector('.site-header')?.classList.toggle('scrolled', window.scrollY > 12), { passive: true });
    return {
        initReveal,
        async signIn(ticket) {
            const response = await fetch('/auth/session', { method: 'POST', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin', body: JSON.stringify({ ticket }) });
            const data = await response.json().catch(() => ({}));
            if (!response.ok) throw new Error(data.error || 'ورود ناموفق بود.');
            return data.redirect;
        },
        async copy(text) { await navigator.clipboard.writeText(text); }
    };
})();
