(() => {
  const docs = {
    'QUICK_START.md': 'Quick Start',
    'TROUBLESHOOTING.md': 'Troubleshooting',
    'VALIDATION_MATRIX.md': 'Validation Matrix',
    'ROADMAP.md': 'Roadmap',
    'DEPLOYMENT.md': 'Deployment'
  };

  const article = document.getElementById('article');
  const title = document.getElementById('doc-title');
  const rawLink = document.getElementById('raw-link');
  const sidebarLinks = Array.from(document.querySelectorAll('[data-doc]'));

  const params = new URLSearchParams(window.location.search);
  const requestedDoc = params.get('doc') || 'QUICK_START.md';
  const doc = Object.prototype.hasOwnProperty.call(docs, requestedDoc) ? requestedDoc : 'QUICK_START.md';

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
    if (href.endsWith('.md') && Object.prototype.hasOwnProperty.call(docs, href)) {
      return `wiki.html?doc=${encodeURIComponent(href)}`;
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

  function setActiveDoc() {
    sidebarLinks.forEach((link) => {
      link.classList.toggle('is-active', link.dataset.doc === doc);
    });
  }

  async function loadDoc() {
    setActiveDoc();
    if (title) title.textContent = docs[doc];
    if (rawLink) rawLink.href = doc;

    try {
      const response = await fetch(doc, { cache: 'no-cache' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const markdown = await response.text();
      article.innerHTML = renderMarkdown(markdown);
      document.title = `${docs[doc]} — ARServer Wiki`;
    } catch (error) {
      article.innerHTML = `
        <div class="error-card">
          <h2>Document could not be rendered</h2>
          <p>The Markdown file <code>${escapeHtml(doc)}</code> could not be loaded from this GitHub Pages site.</p>
          <p><a href="${escapeHtml(doc)}">Open raw Markdown</a></p>
        </div>
      `;
    }
  }

  sidebarLinks.forEach((link) => {
    link.addEventListener('click', (event) => {
      const nextDoc = link.dataset.doc;
      if (!nextDoc || nextDoc === doc) return;
      event.preventDefault();
      window.location.href = `wiki.html?doc=${encodeURIComponent(nextDoc)}`;
    });
  });

  loadDoc();
})();
