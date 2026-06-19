(() => {
  const docs = {
    'QUICK_START.md': { en: 'Quick Start', id: 'Quick Start' },
    'TROUBLESHOOTING.md': { en: 'Troubleshooting', id: 'Troubleshooting' },
    'VALIDATION_MATRIX.md': { en: 'Validation Matrix', id: 'Validation Matrix' },
    'ROADMAP.md': { en: 'Roadmap', id: 'Roadmap' },
    'DEPLOYMENT.md': { en: 'Deployment', id: 'Deployment' }
  };

  const ui = {
    en: {
      lang: 'en',
      skip: 'Skip to document',
      productWiki: 'Product Wiki',
      landing: 'Landing',
      docsKicker: 'Documentation',
      markdownPreview: 'Markdown preview',
      loadingTitle: 'Loading documentation…',
      loadingText: 'Rendering Markdown preview…',
      raw: 'Open raw',
      folder: 'Docs folder',
      errorTitle: 'Document could not be rendered',
      errorBody: 'The Markdown file could not be loaded from this GitHub Pages site.',
      otherLang: 'ID',
      otherLangCode: 'id',
      landingHref: './',
      folderHref: 'https://github.com/masarray/arserver/tree/main/docs'
    },
    id: {
      lang: 'id',
      skip: 'Lewati ke dokumen',
      productWiki: 'Panduan Produk',
      landing: 'Landing ID',
      docsKicker: 'Dokumentasi',
      markdownPreview: 'Preview Markdown',
      loadingTitle: 'Memuat dokumentasi…',
      loadingText: 'Merender preview Markdown…',
      raw: 'Buka raw',
      folder: 'Folder docs ID',
      errorTitle: 'Dokumen tidak dapat dirender',
      errorBody: 'File Markdown tidak dapat dimuat dari GitHub Pages site ini.',
      otherLang: 'EN',
      otherLangCode: 'en',
      landingHref: 'id/',
      folderHref: 'https://github.com/masarray/arserver/tree/main/docs/id/wiki'
    }
  };

  const article = document.getElementById('article');
  const title = document.getElementById('doc-title');
  const rawLink = document.getElementById('raw-link');
  const repoLink = document.getElementById('repo-link');
  const sidebarLinks = Array.from(document.querySelectorAll('[data-doc]'));
  const params = new URLSearchParams(window.location.search);
  const requestedDoc = params.get('doc') || 'QUICK_START.md';
  const doc = Object.prototype.hasOwnProperty.call(docs, requestedDoc) ? requestedDoc : 'QUICK_START.md';

  function inferLanguage() {
    const requested = params.get('lang');
    if (requested === 'id' || requested === 'en') return requested;

    const referrer = document.referrer || '';
    if (/\/arserver\/id\//.test(referrer) || /\/id\//.test(referrer)) return 'id';

    const stored = sessionStorage.getItem('arserverWikiLang');
    if (stored === 'id' || stored === 'en') return stored;

    return 'en';
  }

  const lang = inferLanguage();
  const copy = ui[lang] || ui.en;
  sessionStorage.setItem('arserverWikiLang', lang);
  document.documentElement.lang = copy.lang;

  function docTitle(fileName = doc) {
    return docs[fileName]?.[lang] || docs[fileName]?.en || fileName;
  }

  function docPath(fileName = doc) {
    return lang === 'id' ? `id/wiki/${fileName}` : fileName;
  }

  function wikiUrl(fileName = doc, targetLang = lang) {
    return `wiki.html?lang=${encodeURIComponent(targetLang)}&doc=${encodeURIComponent(fileName)}`;
  }

  function escapeHtml(value = '') {
    return value
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function slugify(value = '') {
    return value
      .toLowerCase()
      .replace(/<[^>]+>/g, '')
      .replace(/[^a-z0-9\s-]/g, '')
      .trim()
      .replace(/\s+/g, '-');
  }

  function normalizeHref(href = '') {
    if (/^(https?:|mailto:|tel:|#)/i.test(href)) return href;
    const clean = href.split('#')[0].split('?')[0];
    const fileName = clean.split('/').pop();
    if (fileName?.endsWith('.md') && Object.prototype.hasOwnProperty.call(docs, fileName)) {
      return wikiUrl(fileName);
    }
    return href;
  }

  function inline(value = '') {
    let text = escapeHtml(value);

    text = text.replace(/!\[([^\]]*)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g, (_, alt, href) => {
      return `<img src="${escapeHtml(normalizeHref(href))}" alt="${escapeHtml(alt)}" loading="lazy" />`;
    });

    text = text.replace(/\[([^\]]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g, (_, label, href) => {
      const safeHref = escapeHtml(normalizeHref(href));
      const isExternal = /^(https?:)?\/\//.test(safeHref);
      return `<a href="${safeHref}"${isExternal ? ' target="_blank" rel="noreferrer"' : ''}>${label}</a>`;
    });

    text = text.replace(/`([^`]+)`/g, '<code>$1</code>');
    text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    text = text.replace(/__([^_]+)__/g, '<strong>$1</strong>');
    text = text.replace(/(^|\s)\*([^*\n]+)\*(?=\s|$|[.,;:!?])/g, '$1<em>$2</em>');
    text = text.replace(/(^|\s)_([^_\n]+)_(?=\s|$|[.,;:!?])/g, '$1<em>$2</em>');

    return text;
  }

  function isTableStart(lines, index) {
    return lines[index]?.includes('|') && /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(lines[index + 1] || '');
  }

  function renderTable(lines, start) {
    const tableLines = [];
    let i = start;
    while (i < lines.length && lines[i].includes('|') && lines[i].trim()) {
      tableLines.push(lines[i]);
      i += 1;
    }

    const rows = tableLines
      .filter((_, rowIndex) => rowIndex !== 1)
      .map((line) => line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((cell) => inline(cell.trim())));

    const [head = [], ...body] = rows;
    const thead = `<thead><tr>${head.map((cell) => `<th>${cell}</th>`).join('')}</tr></thead>`;
    const tbody = `<tbody>${body.map((row) => `<tr>${row.map((cell) => `<td>${cell}</td>`).join('')}</tr>`).join('')}</tbody>`;

    return { html: `<table>${thead}${tbody}</table>`, next: i };
  }

  function renderList(lines, start, ordered) {
    const tag = ordered ? 'ol' : 'ul';
    const items = [];
    let i = start;

    while (i < lines.length) {
      const line = lines[i];
      const match = ordered ? line.match(/^\s*\d+\.\s+(.+)$/) : line.match(/^\s*[-*+]\s+(.+)$/);
      if (!match) break;

      let content = match[1];
      let className = '';
      const task = content.match(/^\[( |x|X)\]\s+(.+)$/);
      if (task) {
        const checked = task[1].toLowerCase() === 'x' ? ' checked' : '';
        content = `<input type="checkbox" disabled${checked} />${inline(task[2])}`;
        className = ' class="task-list-item"';
      } else {
        content = inline(content);
      }

      items.push(`<li${className}>${content}</li>`);
      i += 1;
    }

    return { html: `<${tag}>${items.join('')}</${tag}>`, next: i };
  }

  function renderMarkdown(markdown = '') {
    const lines = markdown.replace(/\r\n/g, '\n').split('\n');
    const html = [];
    let i = 0;

    while (i < lines.length) {
      const line = lines[i];
      const trimmed = line.trim();

      if (!trimmed) {
        i += 1;
        continue;
      }

      if (trimmed.startsWith('```')) {
        const language = trimmed.slice(3).trim();
        const code = [];
        i += 1;
        while (i < lines.length && !lines[i].trim().startsWith('```')) {
          code.push(lines[i]);
          i += 1;
        }
        i += 1;
        html.push(`<pre><code class="language-${escapeHtml(language)}">${escapeHtml(code.join('\n'))}</code></pre>`);
        continue;
      }

      if (/^---+$/.test(trimmed)) {
        html.push('<hr />');
        i += 1;
        continue;
      }

      const heading = trimmed.match(/^(#{1,6})\s+(.+)$/);
      if (heading) {
        const level = heading[1].length;
        const content = inline(heading[2].replace(/\s+#+$/, ''));
        const id = slugify(content);
        html.push(`<h${level} id="${id}">${content}</h${level}>`);
        i += 1;
        continue;
      }

      if (isTableStart(lines, i)) {
        const table = renderTable(lines, i);
        html.push(table.html);
        i = table.next;
        continue;
      }

      if (/^\s*>\s?/.test(line)) {
        const quote = [];
        while (i < lines.length && /^\s*>\s?/.test(lines[i])) {
          quote.push(lines[i].replace(/^\s*>\s?/, ''));
          i += 1;
        }
        html.push(`<blockquote>${renderMarkdown(quote.join('\n'))}</blockquote>`);
        continue;
      }

      if (/^\s*\d+\.\s+/.test(line)) {
        const list = renderList(lines, i, true);
        html.push(list.html);
        i = list.next;
        continue;
      }

      if (/^\s*[-*+]\s+/.test(line)) {
        const list = renderList(lines, i, false);
        html.push(list.html);
        i = list.next;
        continue;
      }

      const paragraph = [trimmed];
      i += 1;
      while (i < lines.length) {
        const next = lines[i].trim();
        if (!next || next.startsWith('```') || /^#{1,6}\s+/.test(next) || isTableStart(lines, i) || /^\s*([-*+]\s+|\d+\.\s+|>\s?)/.test(lines[i])) break;
        paragraph.push(next);
        i += 1;
      }
      html.push(`<p>${inline(paragraph.join(' '))}</p>`);
    }

    return html.join('\n');
  }

  function applyUiLanguage() {
    const skip = document.querySelector('.skip-link');
    const brandSmall = document.querySelector('.wiki-brand small');
    const sidebarKicker = document.querySelector('.sidebar-kicker');
    const navLinks = Array.from(document.querySelectorAll('.wiki-header nav a'));
    const eyebrow = document.querySelector('.doc-eyebrow');

    if (skip) skip.textContent = copy.skip;
    if (brandSmall) brandSmall.textContent = copy.productWiki;
    if (sidebarKicker) sidebarKicker.textContent = copy.docsKicker;
    if (eyebrow) eyebrow.textContent = copy.markdownPreview;
    if (title) title.textContent = copy.loadingTitle;

    if (navLinks[0]) {
      navLinks[0].textContent = copy.landing;
      navLinks[0].href = copy.landingHref;
    }
    if (rawLink) rawLink.textContent = copy.raw;
    if (repoLink) {
      repoLink.textContent = copy.folder;
      repoLink.href = copy.folderHref;
    }

    sidebarLinks.forEach((link) => {
      const fileName = link.dataset.doc;
      link.textContent = docTitle(fileName);
      link.href = wikiUrl(fileName);
    });

    const nav = document.querySelector('.wiki-header nav');
    if (nav && !nav.querySelector('[data-lang-switch]')) {
      const switcher = document.createElement('a');
      switcher.dataset.langSwitch = 'true';
      switcher.href = wikiUrl(doc, copy.otherLangCode);
      switcher.textContent = copy.otherLang;
      nav.appendChild(switcher);
    }

    const loadingText = document.querySelector('.loading-card p');
    if (loadingText) loadingText.textContent = copy.loadingText;
  }

  function setActiveDoc() {
    sidebarLinks.forEach((link) => {
      link.classList.toggle('is-active', link.dataset.doc === doc);
    });
  }

  async function loadDoc() {
    applyUiLanguage();
    setActiveDoc();
    if (title) title.textContent = docTitle(doc);
    if (rawLink) rawLink.href = docPath(doc);

    try {
      const response = await fetch(docPath(doc), { cache: 'no-cache' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const markdown = await response.text();
      article.innerHTML = renderMarkdown(markdown);
      document.title = `${docTitle(doc)} — ARServer Wiki`;
    } catch (error) {
      article.innerHTML = `
        <div class="error-card">
          <h2>${escapeHtml(copy.errorTitle)}</h2>
          <p>${escapeHtml(copy.errorBody)}</p>
          <p><a href="${escapeHtml(docPath(doc))}">${escapeHtml(copy.raw)}</a></p>
        </div>
      `;
    }
  }

  sidebarLinks.forEach((link) => {
    link.addEventListener('click', (event) => {
      const nextDoc = link.dataset.doc;
      if (!nextDoc || nextDoc === doc) return;
      event.preventDefault();
      window.location.href = wikiUrl(nextDoc);
    });
  });

  loadDoc();
})();
