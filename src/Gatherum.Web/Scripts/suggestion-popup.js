// A minimal popup for suggestion menus (mentions, slash commands): a floating list
// positioned at the caret, driven entirely by TipTap's suggestion plugin callbacks.
export class SuggestionPopup {
  constructor(renderItem) {
    this.renderItem = renderItem;
    this.element = document.createElement('div');
    this.element.className = 'suggestion-popup';
    this.items = [];
    this.selected = 0;
    this.command = null;
  }

  open(clientRect) {
    if (!this.element.isConnected) document.body.appendChild(this.element);
    this.position(clientRect);
  }

  position(clientRect) {
    const rect = clientRect?.();
    if (!rect) return;
    this.element.style.left = `${rect.left + window.scrollX}px`;
    this.element.style.top = `${rect.bottom + window.scrollY + 4}px`;
  }

  update(items, command) {
    this.items = items;
    this.command = command;
    this.selected = 0;
    this.render();
  }

  render() {
    this.element.innerHTML = '';
    this.items.forEach((item, index) => {
      const row = document.createElement('button');
      row.className = 'suggestion-item' + (index === this.selected ? ' selected' : '');
      row.innerHTML = this.renderItem(item);
      row.addEventListener('mousedown', (e) => {
        e.preventDefault();
        this.command?.(item);
      });
      this.element.appendChild(row);
    });
    if (this.items.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'suggestion-empty';
      empty.textContent = 'No matches';
      this.element.appendChild(empty);
    }
  }

  onKeyDown(event) {
    if (event.key === 'ArrowDown') {
      this.selected = Math.min(this.selected + 1, this.items.length - 1);
      this.render();
      return true;
    }
    if (event.key === 'ArrowUp') {
      this.selected = Math.max(this.selected - 1, 0);
      this.render();
      return true;
    }
    if (event.key === 'Enter') {
      if (this.items[this.selected]) this.command?.(this.items[this.selected]);
      return true;
    }
    if (event.key === 'Escape') {
      this.close();
      return true;
    }
    return false;
  }

  close() {
    this.element.remove();
  }
}

export function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text;
  return div.innerHTML;
}
