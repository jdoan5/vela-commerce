namespace VelaCommerce.Domain.Orders;

/// <summary>
/// The order lifecycle shown on the demo timeline. Values are explicit so the
/// persisted integers survive reordering of the enum.
/// </summary>
public enum OrderStatus
{
    /// <summary>Created, stock reserved, awaiting payment settlement.</summary>
    Pending = 0,

    /// <summary>Payment captured. Reservations are confirmed at this point.</summary>
    Paid = 1,

    /// <summary>Picked and boxed.</summary>
    Packed = 2,

    /// <summary>Handed to the carrier. Stock has physically left.</summary>
    Shipped = 3,

    /// <summary>Terminal. Reached from Pending or Paid; releases or restocks units.</summary>
    Cancelled = 4
}
