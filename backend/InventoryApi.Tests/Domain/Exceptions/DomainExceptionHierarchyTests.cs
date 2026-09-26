using Inventory.Domain.Exceptions;
using Xunit;

namespace InventoryApi.Tests.Domain.Exceptions;

/// <summary>
/// Issue #163: locks in the shape of the domain exception hierarchy so a future change to it - a
/// new specialization, an accidental unsealing, a base type swap - is a deliberate, reviewed edit
/// here rather than a silent drift. See docs/architecture.md § Domain and application error mapping
/// for the ownership table these types implement.
/// </summary>
public class DomainExceptionHierarchyTests
{
    [Fact]
    public void DomainException_is_the_abstract_root_of_the_hierarchy()
    {
        Assert.True(typeof(DomainException).IsAbstract);
        Assert.Equal(typeof(Exception), typeof(DomainException).BaseType);
    }

    [Fact]
    public void DomainValidationException_derives_from_DomainException_and_is_sealed()
    {
        Assert.Equal(typeof(DomainException), typeof(DomainValidationException).BaseType);
        Assert.True(typeof(DomainValidationException).IsSealed);
    }

    [Fact]
    public void DomainConflictException_derives_from_DomainException_and_is_not_sealed()
    {
        // Not sealed on purpose: InsufficientStockException specializes it below.
        Assert.Equal(typeof(DomainException), typeof(DomainConflictException).BaseType);
        Assert.False(typeof(DomainConflictException).IsSealed);
    }

    [Fact]
    public void InsufficientStockException_specializes_DomainConflictException_and_is_sealed()
    {
        Assert.Equal(typeof(DomainConflictException), typeof(InsufficientStockException).BaseType);
        Assert.True(typeof(InsufficientStockException).IsSealed);

        // It is therefore also a DomainException and a DomainConflictException by transitivity -
        // the shape DomainExceptionHandler's Classify switch and its logging guard depend on.
        Assert.IsAssignableFrom<DomainException>(new InsufficientStockException(0));
        Assert.IsAssignableFrom<DomainConflictException>(new InsufficientStockException(0));
    }

    [Fact]
    public void DomainValidationException_message_is_exactly_what_the_caller_supplied()
    {
        var exception = new DomainValidationException("Quantity must be positive.");

        Assert.Equal("Quantity must be positive.", exception.Message);
    }

    [Fact]
    public void DomainConflictException_message_is_exactly_what_the_caller_supplied()
    {
        var exception = new DomainConflictException("The agreement overlaps an existing agreement for this site.");

        Assert.Equal("The agreement overlaps an existing agreement for this site.", exception.Message);
    }

    [Fact]
    public void InsufficientStockException_message_reports_the_available_stock()
    {
        var exception = new InsufficientStockException(3);

        Assert.Equal("Not enough products in stock. Available stock: 3", exception.Message);
    }

    [Fact]
    public void The_hierarchy_lives_in_Inventory_Domain_Exceptions()
    {
        foreach (var type in new[]
        {
            typeof(DomainException),
            typeof(DomainValidationException),
            typeof(DomainConflictException),
            typeof(InsufficientStockException),
        })
        {
            Assert.Equal("Inventory.Domain.Exceptions", type.Namespace);
            Assert.Same(typeof(Inventory.Domain.AssemblyMarker).Assembly, type.Assembly);
        }
    }
}
