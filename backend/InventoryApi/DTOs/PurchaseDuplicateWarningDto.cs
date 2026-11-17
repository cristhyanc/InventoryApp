namespace InventoryApi.DTOs;

/// <summary>
/// A purchase already recorded that the one being entered looks like (issue #1247): the same
/// supplier, the same invoice number and the same business day.
///
/// <para>It carries only what identifies that purchase to the person entering this one - its id, its
/// title and its date - and nothing about what it cost, what it bought or who supplied it: the
/// warning's job is to let an operator go and look, not to restate a purchase they can open.</para>
/// </summary>
/// <param name="PurchaseId">The recorded purchase, so the form can link to it.</param>
/// <param name="Title">That purchase's own title.</param>
/// <param name="PurchaseDate">The instant it is recorded against.</param>
public sealed record PurchaseDuplicateMatchDto(int PurchaseId, string Title, DateTime PurchaseDate);

/// <summary>
/// The answer to "does this look like a purchase that is already recorded?" (issue #1247).
///
/// <para><see cref="Match"/> is <see langword="null"/> when nothing matches <em>and</em> when the
/// question could not be asked at all - no supplier, no invoice number, or no date - because a
/// warning nobody can act on and no warning are the same thing to the form. It is a warning and
/// never a decision: nothing about saving the purchase depends on it.</para>
/// </summary>
public sealed record PurchaseDuplicateWarningDto(PurchaseDuplicateMatchDto? Match);
