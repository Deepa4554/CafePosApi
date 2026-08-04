using CafePOS.Api.Contracts;
using CafePOS.Api.Data;
using CafePOS.Api.Domain;
using CafePOS.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CafePOS.Api.Controllers;

/// <summary>Vendor master — supplier contact/GST/payment-terms records that PurchaseOrder
/// links to via VendorId, replacing the old free-text SupplierName going forward.</summary>
[ApiController]
[Route("api/vendors")]
[Authorize(Policy = Policies.OwnerOrManager)]
[Authorize(Policy = Policies.RequirePlus)]
// PurchaseOrders too — the PO form needs the vendor list to raise an order at all.
[RequireScreen("Vendors", "PurchaseOrders")]
public class VendorsController(CafePosDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<VendorDto>> List([FromQuery] bool includeInactive = false)
    {
        var query = db.Vendors.AsQueryable();
        if (!includeInactive) query = query.Where(v => v.IsActive);
        return (await query.OrderBy(v => v.Name).ToListAsync()).Select(ToDto);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VendorDto>> Get(int id)
    {
        var vendor = await db.Vendors.FindAsync(id);
        if (vendor is null) return NotFound();
        return ToDto(vendor);
    }

    [HttpPost]
    public async Task<ActionResult<VendorDto>> Create(CreateVendorRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Vendor name is required.");
        if (!string.IsNullOrWhiteSpace(req.Phone) && req.Phone.Trim().Length != 10)
            throw new ApiValidationException("Phone must be a 10-digit number.");

        var vendor = new Vendor
        {
            Name = req.Name.Trim(),
            Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim(),
            Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
            Gstin = string.IsNullOrWhiteSpace(req.Gstin) ? null : req.Gstin.Trim(),
            Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim(),
            PaymentTerms = string.IsNullOrWhiteSpace(req.PaymentTerms) ? null : req.PaymentTerms.Trim(),
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
        };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = vendor.Id }, ToDto(vendor));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<VendorDto>> Update(int id, UpdateVendorRequest req)
    {
        var vendor = await db.Vendors.FindAsync(id);
        if (vendor is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ApiValidationException("Vendor name is required.");
        if (!string.IsNullOrWhiteSpace(req.Phone) && req.Phone.Trim().Length != 10)
            throw new ApiValidationException("Phone must be a 10-digit number.");

        vendor.Name = req.Name.Trim();
        vendor.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
        vendor.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        vendor.Gstin = string.IsNullOrWhiteSpace(req.Gstin) ? null : req.Gstin.Trim();
        vendor.Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();
        vendor.PaymentTerms = string.IsNullOrWhiteSpace(req.PaymentTerms) ? null : req.PaymentTerms.Trim();
        vendor.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        vendor.IsActive = req.IsActive;
        await db.SaveChangesAsync();
        return ToDto(vendor);
    }

    /// <summary>Deactivate rather than delete — past PurchaseOrders keep pointing at this
    /// vendor (VendorId FK is SetNull-on-delete only as defense-in-depth, not the normal path).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var vendor = await db.Vendors.FindAsync(id);
        if (vendor is null) return NotFound();
        vendor.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static VendorDto ToDto(Vendor v) =>
        new(v.Id, v.Name, v.Phone, v.Email, v.Gstin, v.Address, v.PaymentTerms, v.Notes, v.IsActive, v.CreatedAt);
}
