(() => {
  const root = document.documentElement;
  const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  root.classList.add('is-ready');

  const header = document.querySelector('.site-header');
  let lastScrollY = window.scrollY;

  function updateHeaderState() {
    const current = window.scrollY;
    header?.classList.toggle('is-scrolled', current > 12);

    if (header && current > 420 && current > lastScrollY + 10) {
      header.classList.add('is-hidden');
    } else if (header) {
      header.classList.remove('is-hidden');
    }

    lastScrollY = current;
  }

  updateHeaderState();
  window.addEventListener('scroll', updateHeaderState, { passive: true });

  const revealSelectors = [
    '.hero-content > *',
    '.hero-console',
    '.section-heading > *',
    '.audience-grid article',
    '.timeline article',
    '.split > *',
    '.feature-grid article',
    '.shot-card',
    '.wiki-grid a',
    '.scope-list article',
    '.safety > *',
    '.faq-list details',
    '.final-cta > *',
    '.site-footer > *'
  ];

  const revealItems = Array.from(document.querySelectorAll(revealSelectors.join(',')))
    .filter((item, index, array) => array.indexOf(item) === index);

  const grouped = new Map();
  revealItems.forEach((item) => {
    const group = item.closest('.section, .hero-section, .final-cta, .site-footer') || document.body;
    if (!grouped.has(group)) grouped.set(group, []);
    grouped.get(group).push(item);
  });

  grouped.forEach((items) => {
    items.forEach((item, index) => {
      item.classList.add('reveal-item');
      item.style.setProperty('--reveal-delay', Math.min(index, 8));
    });
  });

  if (prefersReducedMotion) {
    revealItems.forEach((item) => item.classList.add('is-visible'));
  } else {
    const revealObserver = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-visible');
          revealObserver.unobserve(entry.target);
        }
      });
    }, {
      threshold: 0.12,
      rootMargin: '0px 0px -8% 0px'
    });

    revealItems.forEach((item) => revealObserver.observe(item));
  }

  const navLinks = Array.from(document.querySelectorAll('.nav-links a[href^="#"]'));
  const sectionMap = navLinks
    .map((link) => {
      const section = document.querySelector(link.getAttribute('href'));
      return section ? { link, section } : null;
    })
    .filter(Boolean);

  if (sectionMap.length) {
    const navObserver = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        const match = sectionMap.find((item) => item.section === entry.target);
        if (!match) return;
        navLinks.forEach((link) => link.classList.remove('is-active'));
        match.link.classList.add('is-active');
      });
    }, {
      threshold: 0.3,
      rootMargin: '-22% 0px -58% 0px'
    });

    sectionMap.forEach(({ section }) => navObserver.observe(section));
  }

  navLinks.forEach((link) => {
    link.addEventListener('click', (event) => {
      const target = document.querySelector(link.getAttribute('href'));
      if (!target) return;
      event.preventDefault();
      target.scrollIntoView({ behavior: prefersReducedMotion ? 'auto' : 'smooth', block: 'start' });
    });
  });

  function addRipple(event) {
    const target = event.currentTarget;
    if (!(target instanceof HTMLElement) || prefersReducedMotion) return;
    const rect = target.getBoundingClientRect();
    const ripple = document.createElement('span');
    ripple.className = 'press-ripple';
    ripple.style.left = `${event.clientX - rect.left}px`;
    ripple.style.top = `${event.clientY - rect.top}px`;
    target.appendChild(ripple);
    window.setTimeout(() => ripple.remove(), 620);
  }

  document.querySelectorAll('.button, .nav-cta').forEach((item) => {
    item.addEventListener('pointerdown', addRipple);
  });

  const lightbox = document.createElement('div');
  lightbox.className = 'image-lightbox';
  lightbox.setAttribute('aria-hidden', 'true');
  lightbox.innerHTML = `
    <div class="image-lightbox__panel" role="dialog" aria-modal="true" aria-label="Screenshot preview">
      <div class="image-lightbox__topbar">
        <strong>ARServer screenshot</strong>
        <button class="image-lightbox__close" type="button" aria-label="Close screenshot preview">×</button>
      </div>
      <img alt="Expanded ARServer screenshot" />
    </div>
  `;
  document.body.appendChild(lightbox);

  const lightboxImage = lightbox.querySelector('img');
  const lightboxTitle = lightbox.querySelector('strong');
  const lightboxClose = lightbox.querySelector('.image-lightbox__close');

  function openLightbox(src, alt, title) {
    if (!lightboxImage) return;
    lightboxImage.src = src;
    lightboxImage.alt = alt || 'Expanded ARServer screenshot';
    if (lightboxTitle) lightboxTitle.textContent = title || 'ARServer screenshot';
    lightbox.classList.add('is-open');
    lightbox.setAttribute('aria-hidden', 'false');
    document.body.style.overflow = 'hidden';
    lightboxClose?.focus({ preventScroll: true });
  }

  function closeLightbox() {
    lightbox.classList.remove('is-open');
    lightbox.setAttribute('aria-hidden', 'true');
    document.body.style.overflow = '';
  }

  document.querySelectorAll('.shot-card a[href$=".webp"], .shot-card a[href$=".png"], [data-full]').forEach((trigger) => {
    trigger.addEventListener('click', (event) => {
      const image = trigger.querySelector('img');
      const src = trigger.dataset?.full || trigger.getAttribute('href');
      const title = trigger.closest('.shot-card')?.querySelector('figcaption strong')?.textContent?.trim();
      if (!src) return;
      event.preventDefault();
      openLightbox(src, image?.alt, title);
    });
  });

  lightboxClose?.addEventListener('click', closeLightbox);
  lightbox.addEventListener('click', (event) => {
    if (event.target === lightbox) closeLightbox();
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && lightbox.classList.contains('is-open')) closeLightbox();
  });
})();
