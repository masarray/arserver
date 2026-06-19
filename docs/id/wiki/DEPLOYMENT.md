# Deployment dan GitHub Pages

ARServer adalah aplikasi desktop Windows. Folder `docs/` menyediakan landing page produk publik untuk GitHub Pages.

## GitHub Pages

Static landing page berada di:

```text
docs/index.html
```

Pages workflow berada di:

```text
.github/workflows/pages.yml
```

Workflow tersebut mempublikasikan folder `docs/` ke GitHub Pages.

Setting GitHub Pages yang disarankan:

- Source: **GitHub Actions**
- URL: `https://masarray.github.io/arserver/`

## File SEO

Landing page menyertakan:

- `docs/robots.txt`
- `docs/sitemap.xml`
- `docs/site.webmanifest`
- canonical URL
- Open Graph metadata
- Twitter Card metadata
- SoftwareApplication JSON-LD
- struktur bilingual English/Indonesia
- halaman learning, use case, dan panduan statis

## Update screenshot

Letakkan screenshot yang sudah dioptimasi di:

```text
docs/assets/screenshots/
```

Gunakan nama file dan alt text yang deskriptif. Screenshot sebaiknya menunjukkan workflow aplikasi sungguhan: start workspace, IEC values, Modbus map, MQTT topics, dan acquisition controls.
