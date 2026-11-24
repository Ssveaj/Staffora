export function attachComposer(textarea) {
  if (!textarea || textarea.dataset.enterPosts === "attached") return;
  textarea.dataset.enterPosts = "attached";
  textarea.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.altKey && !event.isComposing) {
      event.preventDefault();
    }
  });
}
