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
}

public enum ReservationStatus
{
    Held = 0,
    Confirmed = 1,
    Released = 2
}
