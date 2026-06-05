# Deployment and GitHub Pages

ARServer is a Windows desktop application. The `docs/` folder provides the public product landing page for GitHub Pages.

## GitHub Pages

The static landing page is located at:

```text
docs/index.html
```

The Pages workflow is located at:

```text
.github/workflows/pages.yml
```

It publishes the `docs/` folder to GitHub Pages.

Recommended repository Pages settings:

- Source: **GitHub Actions**
- URL: `https://masarray.github.io/arserver/`

## SEO files

The landing page includes:

- `docs/robots.txt`
- `docs/sitemap.xml`
- `docs/site.webmanifest`
- canonical URL
- Open Graph metadata
- Twitter Card metadata
- SoftwareApplication JSON-LD
- FAQPage JSON-LD

## Updating screenshots

Place optimized screenshots under:

```text
docs/assets/screenshots/
```

Use descriptive file names and alt text. Screenshots should show real app workflows: start workspace, IEC values, Modbus map, MQTT topics, and acquisition controls.
