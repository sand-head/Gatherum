import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import Link from '@tiptap/extension-link';
import Image from '@tiptap/extension-image';
import Placeholder from '@tiptap/extension-placeholder';
import Table from '@tiptap/extension-table';
import TableRow from '@tiptap/extension-table-row';
import TableCell from '@tiptap/extension-table-cell';
import TableHeader from '@tiptap/extension-table-header';
import TaskList from '@tiptap/extension-task-list';
import TaskItem from '@tiptap/extension-task-item';
import Mention from '@tiptap/extension-mention';
import Collaboration from '@tiptap/extension-collaboration';
import CollaborationCursor from '@tiptap/extension-collaboration-cursor';
import * as Y from 'yjs';
import { WebsocketProvider } from 'y-websocket';
import { Callout } from './callout.js';
import { SlashCommands } from './slash-commands.js';
import { SuggestionPopup, escapeHtml } from './suggestion-popup.js';

const AUTOSAVE_DELAY = 1200;

export function mount(element, options, dotnet) {
  const ydoc = new Y.Doc();
  const wsBase = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/collab`;
  const provider = new WebsocketProvider(wsBase, options.nodeId, ydoc);

  let saveTimer = null;
  const editor = new Editor({
    element,
    extensions: [
      StarterKit.configure({ history: false }),
      Link.configure({ openOnClick: false }),
      Image,
      Placeholder.configure({ placeholder: "Write, or type '/' for blocks…" }),
      Table.configure({ resizable: false }), TableRow, TableCell, TableHeader,
      TaskList, TaskItem.configure({ nested: true }),
      Callout,
      SlashCommands(() => pickImage(editor, options.nodeId)),
      MentionExtension(),
      Collaboration.configure({ document: ydoc }),
      CollaborationCursor.configure({ provider, user: options.user }),
    ],
    onUpdate: () => {
      dotnet.invokeMethodAsync('OnEditorDirty');
      clearTimeout(saveTimer);
      saveTimer = setTimeout(async () => {
        await dotnet.invokeMethodAsync('OnAutosave', JSON.stringify(editor.getJSON()));
      }, AUTOSAVE_DELAY);
    },
  });

  // A fresh collaboration doc is empty; seed it from the stored body once the first
  // client has synced. Anyone else arriving later sees non-empty content and skips this.
  provider.once('synced', () => {
    const fragment = ydoc.getXmlFragment('default');
    if (fragment.length === 0 && options.docJson) {
      const content = JSON.parse(options.docJson);
      if (content.content?.some((n) => n.content || n.type !== 'paragraph')) {
        editor.commands.setContent(content);
      }
    }
  });

  return {
    getJson: () => JSON.stringify(editor.getJSON()),
    flush: async () => {
      clearTimeout(saveTimer);
      await dotnet.invokeMethodAsync('OnAutosave', JSON.stringify(editor.getJSON()));
    },
    destroy: () => {
      clearTimeout(saveTimer);
      editor.destroy();
      provider.destroy();
      ydoc.destroy();
    },
  };
}

function MentionExtension() {
  return Mention.configure({
    HTMLAttributes: { class: 'mention' },
    renderLabel: ({ node }) => `@${node.attrs.label}`,
    suggestion: {
      items: async ({ query }) => {
        if (!query) return [];
        const response = await fetch(
          `/api/search?query=${encodeURIComponent(query)}&limit=8`,
          { credentials: 'same-origin' });
        if (!response.ok) return [];
        return await response.json();
      },
      render: () => {
        let popup = null;
        return {
          onStart: (props) => {
            popup = new SuggestionPopup((item) =>
              `<span class="kind">${escapeHtml(item.kind)}</span><span>${escapeHtml(item.title)}</span>`);
            popup.open(props.clientRect);
            popup.update(props.items, (item) =>
              props.command({ id: item.id, label: item.title }));
          },
          onUpdate: (props) => {
            popup?.update(props.items, (item) =>
              props.command({ id: item.id, label: item.title }));
            popup?.position(props.clientRect);
          },
          onKeyDown: (props) => popup?.onKeyDown(props.event) ?? false,
          onExit: () => { popup?.close(); popup = null; },
        };
      },
    },
  });
}

// Uploading an image creates a File node under the current page, then embeds it.
function pickImage(editor, pageNodeId) {
  const input = document.createElement('input');
  input.type = 'file';
  input.accept = 'image/*';
  input.onchange = async () => {
    const file = input.files?.[0];
    if (!file) return;
    const body = new FormData();
    body.append('file', file);
    const response = await fetch(`/api/files?parentId=${pageNodeId}`, {
      method: 'POST', body, credentials: 'same-origin',
    });
    if (!response.ok) return;
    const node = await response.json();
    editor.chain().focus()
      .setImage({ src: `/api/files/${node.id}/content`, alt: file.name })
      .run();
  };
  input.click();
}
