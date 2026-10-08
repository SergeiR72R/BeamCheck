namespace BeamCheck.Core.Model
{
    /// <summary>Role of a drawing object in the load path.</summary>
    public enum ElementKind
    {
        Unknown = 0,
        /// <summary>The supporting beam the standards stand on.</summary>
        Beam,
        /// <summary>Vertical standard section (0.5–2 m); stacked sections form a column.</summary>
        Standard,
        /// <summary>Horizontal ledger/transom between two standards; decks bear on it.</summary>
        Ledger,
        /// <summary>Deck/platform unit spanning between two ledgers.</summary>
        Deck,
        /// <summary>Diagonal brace; only its self-weight is taken (half to each end).</summary>
        Diagonal,
        /// <summary>Guardrail, toe board etc.; self-weight split to its end nodes.</summary>
        Accessory,
    }
}
