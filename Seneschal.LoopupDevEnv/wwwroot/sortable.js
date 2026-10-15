// Drag and drop ordering for the rows of a section.
//
// Blazor Server cannot start a native drag by itself (Firefox needs dataTransfer.setData inside the dragstart event), so this small
// module listens on the section and tells the component where the row was dropped. It never moves rows itself: the component saves
// the new order and Blazor draws the list again.
//
// A row is anything inside the section with data-sort-id. Dropping on the upper half of another row puts the dragged one before it,
// the lower half after it.

const sections = new Map();

export function init(sectionId, dotnet) {
    dispose(sectionId);
    const section = document.getElementById(sectionId);
    if (!section) return;

    let dragged = null;
    let target = null;
    let after = false;

    const rowOf = node => (node && node.closest ? node.closest('[data-sort-id]') : null);
    const inside = row => row && section.contains(row);

    function clear() {
        section.querySelectorAll('.drop-before, .drop-after, .is-dragging').forEach(el => el.classList.remove('drop-before', 'drop-after', 'is-dragging'));
        dragged = null;
        target = null;
    }

    const onDragStart = e => {
        const row = rowOf(e.target);
        if (!inside(row)) return;
        dragged = row;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', row.dataset.sortId);
        // The browser takes its drag picture right after this event, so the row is dimmed a moment later
        requestAnimationFrame(() => row.classList.add('is-dragging'));
    };

    const onDragOver = e => {
        if (!dragged) return;
        const row = rowOf(e.target);
        if (!inside(row)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        section.querySelectorAll('.drop-before, .drop-after').forEach(el => el.classList.remove('drop-before', 'drop-after'));
        if (row === dragged) { target = null; return; }
        const box = row.getBoundingClientRect();
        after = e.clientY > box.top + box.height / 2;
        target = row;
        row.classList.add(after ? 'drop-after' : 'drop-before');
    };

    const onDrop = e => {
        if (!dragged) return;
        e.preventDefault();
        const from = dragged.dataset.sortId;
        const to = target ? target.dataset.sortId : null;
        const place = after;
        clear();
        if (to && to !== from) dotnet.invokeMethodAsync('Reorder', from, to, place);
    };

    section.addEventListener('dragstart', onDragStart);
    section.addEventListener('dragover', onDragOver);
    section.addEventListener('drop', onDrop);
    section.addEventListener('dragend', clear);
    sections.set(sectionId, () => {
        section.removeEventListener('dragstart', onDragStart);
        section.removeEventListener('dragover', onDragOver);
        section.removeEventListener('drop', onDrop);
        section.removeEventListener('dragend', clear);
    });
}

export function dispose(sectionId) {
    const remove = sections.get(sectionId);
    if (remove) remove();
    sections.delete(sectionId);
}
