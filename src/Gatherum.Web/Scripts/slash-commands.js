import { Extension } from '@tiptap/core';
import Suggestion from '@tiptap/suggestion';
import { SuggestionPopup, escapeHtml } from './suggestion-popup.js';

const commands = [
  { title: 'Heading 1', hint: '#', run: (c) => c.setNode('heading', { level: 1 }) },
  { title: 'Heading 2', hint: '##', run: (c) => c.setNode('heading', { level: 2 }) },
  { title: 'Heading 3', hint: '###', run: (c) => c.setNode('heading', { level: 3 }) },
  { title: 'Bullet list', hint: '-', run: (c) => c.toggleBulletList() },
  { title: 'Numbered list', hint: '1.', run: (c) => c.toggleOrderedList() },
  { title: 'Task list', hint: '[ ]', run: (c) => c.toggleTaskList() },
  { title: 'Table', hint: '', run: (c) => c.insertTable({ rows: 3, cols: 3, withHeaderRow: true }) },
  { title: 'Code block', hint: '```', run: (c) => c.toggleCodeBlock() },
  { title: 'Quote', hint: '>', run: (c) => c.toggleBlockquote() },
  { title: 'Callout: info', hint: '', run: (c) => c.setCallout('info') },
  { title: 'Callout: warning', hint: '', run: (c) => c.setCallout('warning') },
  { title: 'Callout: tip', hint: '', run: (c) => c.setCallout('tip') },
  { title: 'Divider', hint: '---', run: (c) => c.setHorizontalRule() },
  { title: 'Image…', hint: '', run: null },
];

export function SlashCommands(pickImage) {
  return Extension.create({
    name: 'slashCommands',

    addProseMirrorPlugins() {
      const editor = this.editor;
      let popup = null;
      return [
        Suggestion({
          editor,
          char: '/',
          startOfLine: false,
          command: ({ editor, range, props }) => {
            const chain = editor.chain().focus().deleteRange(range);
            if (props.run) props.run(chain);
            chain.run();
            if (!props.run) pickImage();
          },
          items: ({ query }) =>
            commands.filter((c) => c.title.toLowerCase().includes(query.toLowerCase())).slice(0, 10),
          render: () => ({
            onStart: (props) => {
              popup = new SuggestionPopup((item) =>
                `<span>${escapeHtml(item.title)}</span><kbd>${escapeHtml(item.hint)}</kbd>`);
              popup.open(props.clientRect);
              popup.update(props.items, props.command);
            },
            onUpdate: (props) => {
              popup?.update(props.items, props.command);
              popup?.position(props.clientRect);
            },
            onKeyDown: (props) => popup?.onKeyDown(props.event) ?? false,
            onExit: () => { popup?.close(); popup = null; },
          }),
        }),
      ];
    },
  });
}
