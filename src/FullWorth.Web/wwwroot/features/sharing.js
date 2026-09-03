// Sharing & members (multi-user account sharing). Any member sees the roster; a space owner can invite
// people (by generating a one-time claim link), revoke pending invites, and remove members. Access to
// individual accounts is granted per invite and enforced server-side by the AccountOwner model.
let ctx;

export function bindSharing(context) {
  ctx = context;
  const inviteBtn = ctx.$('#sharing-invite');
  if (inviteBtn) inviteBtn.addEventListener('click', () => openInviteDialog());
}

function spaceId() {
  return localStorage.getItem('finance.space') || '';
}

export async function renderSharing(context) {
  ctx = context;
  const box = ctx.$('#sharing-members');
  const invitesBox = ctx.$('#sharing-invites');
  const inviteBtn = ctx.$('#sharing-invite');
  if (!box) return;

  const sid = spaceId();
  if (!sid) { box.innerHTML = ''; if (invitesBox) invitesBox.innerHTML = ''; return; }

  let members;
  try { members = await ctx.api(`api/fullworth-spaces/${sid}/members`) || []; }
  catch { box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('common.error'))}</div>`; return; }

  // Owner probe: the invites list is owner-only, so a clean 200 means the caller may manage sharing.
  let invites = [];
  let isOwner = false;
  try { invites = await ctx.api(`api/fullworth-spaces/${sid}/invites`) || []; isOwner = true; }
  catch { isOwner = false; }

  if (inviteBtn) inviteBtn.hidden = !isOwner;

  box.innerHTML = members.length
    ? members.map(m => memberRow(m, isOwner)).join('')
    : `<div class="row-sub">${ctx.esc(ctx.get('sharing.noMembers'))}</div>`;
  box.querySelectorAll('[data-remove-member]').forEach(button =>
    button.addEventListener('click', () => removeMember(sid, button.dataset.removeMember, button.dataset.name)));

  if (invitesBox) {
    invitesBox.hidden = !isOwner || invites.length === 0;
    invitesBox.innerHTML = isOwner && invites.length
      ? `<div class="row-sub panel-intro">${ctx.esc(ctx.get('sharing.pendingInvites'))}</div>` + invites.map(inviteRow).join('')
      : '';
    invitesBox.querySelectorAll('[data-revoke]').forEach(button =>
      button.addEventListener('click', () => revokeInvite(sid, button.dataset.revoke)));
  }
}

function memberRow(member, isOwner) {
  const role = member.role === 'owner' ? ctx.get('sharing.roleOwner') : ctx.get('sharing.roleMember');
  const remove = isOwner
    ? `<button type="button" class="ghost danger" data-remove-member="${ctx.esc(member.userId)}" data-name="${ctx.esc(member.displayName || member.email)}">${ctx.esc(ctx.get('sharing.remove'))}</button>`
    : '';
  return `<div class="row"><div class="row-main"><div class="row-title">${ctx.esc(member.displayName || member.email)}</div><div class="row-sub">${ctx.esc(member.email)} · ${ctx.esc(role)}</div></div><div class="row-side">${remove}</div></div>`;
}

function inviteRow(invite) {
  const role = invite.spaceRole === 'owner' ? ctx.get('sharing.roleOwner') : ctx.get('sharing.roleMember');
  const expires = ctx.get('sharing.expiresOn').replace('{date}', ctx.date(invite.expiresAt));
  return `<div class="row"><div class="row-main"><div class="row-title">${ctx.esc(invite.email)}</div><div class="row-sub">${ctx.esc(role)} · ${ctx.esc(expires)}</div></div><div class="row-side"><button type="button" class="ghost danger" data-revoke="${ctx.esc(invite.id)}">${ctx.esc(ctx.get('sharing.revoke'))}</button></div></div>`;
}

async function removeMember(sid, userId, name) {
  if (!await ctx.confirm(ctx.get('sharing.removeConfirm').replace('{name}', name || ''), { destructive: true, confirmLabel: ctx.get('sharing.remove') })) return;
  try {
    await ctx.api(`api/fullworth-spaces/${sid}/members/${userId}`, { method: 'DELETE' });
    ctx.toast(ctx.get('common.saved'));
    await renderSharing(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

async function revokeInvite(sid, inviteId) {
  if (!await ctx.confirm(ctx.get('sharing.revokeConfirm'), { destructive: true, confirmLabel: ctx.get('sharing.revoke') })) return;
  try {
    await ctx.api(`api/fullworth-spaces/${sid}/invites/${inviteId}`, { method: 'DELETE' });
    ctx.toast(ctx.get('common.saved'));
    await renderSharing(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

async function openInviteDialog() {
  const sid = spaceId();
  if (!sid) return;

  let accounts = [];
  try { accounts = (await ctx.api('api/accounts')) || []; } catch { /* accounts optional */ }

  const accountRows = accounts.map(a => `<label class="check"><input type="checkbox" class="share-acct" value="${ctx.esc(a.id)}">
    <span>${ctx.esc(a.displayName || a.institutionName || a.id)}</span>
    <select class="share-level" aria-label="${ctx.esc(ctx.get('sharing.shareAccount'))}">
      <option value="viewer">${ctx.esc(ctx.get('sharing.accessViewer'))}</option>
      <option value="owner">${ctx.esc(ctx.get('sharing.accessOwner'))}</option>
    </select></label>`).join('');

  const dlg = ctx.dialog(`<form class="dialog-card" method="dialog">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('sharing.invite'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('sharing.inviteEmail'))}<input name="email" type="email" required autocomplete="off"></label>
    <label>${ctx.esc(ctx.get('sharing.role'))}<span class="field-inline"><select name="role">
      <option value="member">${ctx.esc(ctx.get('sharing.roleMember'))}</option>
      <option value="owner">${ctx.esc(ctx.get('sharing.roleOwner'))}</option>
    </select></span></label>
    ${accounts.length ? `<fieldset class="share-accounts"><legend>${ctx.esc(ctx.get('sharing.shareAccounts'))}</legend>${accountRows}</fieldset>` : ''}
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="button" data-send>${ctx.esc(ctx.get('sharing.invite'))}</button></div>
    <div class="share-link-box" hidden><p class="row-sub">${ctx.esc(ctx.get('sharing.claimLink'))}</p><div class="field-inline"><input class="share-link" readonly><button type="button" class="ghost" data-copy>${ctx.esc(ctx.get('sharing.copyLink'))}</button></div></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();

  dlg.querySelector('[data-send]').onclick = async () => {
    const email = dlg.querySelector('[name=email]').value.trim();
    if (!email) { dlg.querySelector('[name=email]').focus(); return; }
    const role = dlg.querySelector('[name=role]').value;
    const grants = [...dlg.querySelectorAll('.share-acct')].filter(c => c.checked).map(c => ({
      accountId: c.value,
      ownershipType: c.closest('label').querySelector('.share-level').value
    }));
    try {
      const res = await ctx.api(`api/fullworth-spaces/${sid}/invites`, ctx.jsonBody({ email, role, accounts: grants }));
      const link = `${location.origin}/auth/claim?token=${encodeURIComponent(res.claimToken)}`;
      const box = dlg.querySelector('.share-link-box');
      box.hidden = false;
      box.querySelector('.share-link').value = link;
      box.querySelector('[data-copy]').onclick = async () => {
        try { await navigator.clipboard.writeText(link); ctx.toast(ctx.get('sharing.linkCopied')); }
        catch { dlg.querySelector('.share-link').select(); }
      };
      ctx.toast(ctx.get('sharing.inviteSent'));
      await renderSharing(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };

  dlg.showModal();
}
