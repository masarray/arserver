const dialog = document.getElementById('imageDialog');
const dialogImage = dialog?.querySelector('img');
const closeButton = dialog?.querySelector('.dialog-close');

function openPreview(src, alt) {
  if (!dialog || !dialogImage) return;
  dialogImage.src = src;
  dialogImage.alt = alt || 'Expanded ARServer screenshot';
  if (typeof dialog.showModal === 'function') dialog.showModal();
}

document.querySelectorAll('[data-full]').forEach((button) => {
  button.addEventListener('click', () => {
    const image = button.querySelector('img');
    openPreview(button.dataset.full, image?.alt);
  });
});

closeButton?.addEventListener('click', () => dialog.close());
dialog?.addEventListener('click', (event) => {
  if (event.target === dialog) dialog.close();
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && dialog?.open) dialog.close();
});
