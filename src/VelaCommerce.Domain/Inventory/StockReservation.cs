using VelaCommerce.Domain.Common;

namespace VelaCommerce.Domain.Inventory;

/// <summary>
/// An expiring claim on stock held between "checkout started" and "payment settled".
/// A reaper releases the ones that lapse, which is what stops an abandoned checkout
/// from holding the last unit forever.
/// </summary>
public sealed class StockReservation : Entity
{
    private StockReservation() { } // EF

    public StockReservation(Guid variantId, Guid orderId, int quantity, DateTimeOffset expiresAt)
    {
        if (quantity <= 0) throw new DomainException("Reservation quantity must be positive.");
        VariantId = variantId;
        OrderId = orderId;
        Quantity = quantity;
        ExpiresAt = expiresAt;
        Status = ReservationStatus.Held;
    }

    public Guid VariantId { get; private set; }
    public Guid OrderId { get; private set; }
    public int Quantity { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public ReservationStatus Status { get; private set; }

    public bool HasLapsed(DateTimeOffset now) => Status == ReservationStatus.Held && now >= ExpiresAt;

    public void Confirm()
    {
        if (Status != ReservationStatus.Held)
            throw new DomainException($"Only a held reservation can be confirmed, this one is {Status}.");
        Status = ReservationStatus.Confirmed;
    }

    public void Release()
    {
        if (Status == ReservationStatus.Confirmed)
            throw new DomainException("A confirmed reservation cannot be released; cancel the order instead.");
        Status = ReservationStatus.Released;
    }

    /// <summary>
    /// Gives the units back because the order that claimed them was cancelled.
    /// <para>
    /// This is the path <see cref="Release"/>'s refusal has always pointed at — "cancel the order
    /// instead" named a caller that did not exist until refunds arrived. A confirmed reservation
    /// belongs to a paid order, and a paid order that is cancelled has had its money returned, so
    /// the goods must stop being promised to it.
    /// </para>
    /// <para>
    /// Safe only while the units are still on the ledger, which is true for every order a
    /// cancellation can reach: Pending and Paid both hold their units as <c>reserved</c>, and the
    /// two statuses whose parcels have moved — Packed and Shipped — have no legal edge to
    /// Cancelled at all.
    /// </para>
    /// </summary>
    public void ReturnOnCancellation()
    {
        if (Status == ReservationStatus.Released)
            throw new DomainException("This reservation has already been released; releasing it again would invent stock.");

        Status = ReservationStatus.Released;
    }
}

public enum ReservationStatus
{
    Held = 0,
    Confirmed = 1,
    Released = 2
}
