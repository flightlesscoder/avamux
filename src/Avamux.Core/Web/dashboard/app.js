// Example dashboard script. `avamux.command(name, args)` runs the same commands as the avamux CLI.
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

async function refresh() {
  try {
    const terminals = await avamux.command('terminal list');
    $('terminals').innerHTML = terminals.length
      ? terminals.map(t => `<li><span class="dot ${t.running ? 'ok' : ''}"></span><a href="#" data-term="${esc(t.id)}">${esc(t.title)}</a> <span class="muted">${esc(t.cwd)}</span></li>`).join('')
      : '<li class="muted">No terminals open</li>';

    const watches = await avamux.command('watch list');
    $('watches').innerHTML = watches.length
      ? watches.map(w => `<li><span class="dot ${esc(w.status)}"></span><span>${w.link ? `<a href="${esc(w.link)}" target="_blank">${esc(w.name)}</a>` : esc(w.name)}<br><span class="muted">${esc(w.summary)}</span></span></li>`).join('')
      : '<li class="muted">Nothing is being watched</li>';

    const events = await avamux.command('event agenda', { days: 7 });
    $('events').innerHTML = events.length
      ? events.slice(0, 8).map(e => `<li><span class="muted">${esc(e.start.replace('T', ' ').slice(0, 16))}</span> ${esc(e.title)}</li>`).join('')
      : '<li class="muted">Nothing in the next 7 days</li>';
  } catch (err) {
    $('terminals').innerHTML = `<li class="muted">${esc(err.message)}</li>`;
  }
}

$('new-terminal').onclick = async () => { await avamux.command('terminal new'); await avamux.command('app nav', { page: 'terminals' }); };
$('check-watches').onclick = async () => { await avamux.command('watch check'); refresh(); };
$('terminals').onclick = async ev => {
  const a = ev.target.closest('a[data-term]');
  if (!a) return;
  ev.preventDefault();
  await avamux.command('terminal select', { id: a.dataset.term });
  await avamux.command('app nav', { page: 'terminals' });
};

refresh();
setInterval(refresh, 5000);
