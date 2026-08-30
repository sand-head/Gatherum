namespace Gatherum.Client;

/// <summary>
/// The words a shared list is read with. One mechanism, several questions: "who has which
/// sprite" and "who can make which night" are the same grid — a row per thing, a column
/// per person, a mark where that person says yes — and the only difference is what a mark
/// <em>means</em>. So the fence opens with a word, and this is what that word buys.
///
/// The shape <see cref="CalloutExtension.Kinds"/> already established: one implementation,
/// a small vocabulary, a new entry costs a line rather than a component. The set here has
/// to match <see cref="Gatherum.Core.Markdown.CollectionSyntax.Kinds"/>, which is the half
/// that parses — <c>CollectionSyntaxTests</c> keeps the two honest.
/// </summary>
/// <param name="Rows">What the first column is called: the noun a row is.</param>
/// <param name="Total">How big the list is, said in the header — <c>{0}</c> is the
/// count, and the rest is whatever this question calls its rows.</param>
/// <param name="Score">Where the reader stands, said in the footer: <c>{0}</c> theirs,
/// <c>{1}</c> the total, <c>{2}</c> the difference.</param>
/// <param name="Invite">What to say to somebody who could answer and has not.</param>
/// <param name="Yes">A mark, for a screen reader: what this person is saying.</param>
/// <param name="No">And what an empty one says.</param>
/// <param name="Tallies">Whether a row's own total is worth a column. "How many can make
/// Friday" is the question an availability list is read for and a poll <em>is</em>; "how
/// many people have Sonic" is a curiosity beside "how many do I still need", so a
/// collection spends the width on rows instead.</param>
/// <param name="PicksOne">Whether a person has one answer rather than many. Presentation
/// only — radio buttons rather than checkboxes; the rule itself is
/// <see cref="Gatherum.Core.Markdown.CollectionSyntax.PicksOne"/>, enforced where the
/// file is written.</param>
/// <param name="NamesAnswers">Whether the grid says who answered what. Presentation only
/// again: <see cref="Gatherum.Core.Markdown.CollectionSyntax.NamesAnswers"/> is what
/// actually withholds the columns, and this decides whether to tell the reader so.</param>
public sealed record ListVocabulary(
    string Rows,
    string Total,
    string Score,
    string Invite,
    string Yes,
    string No,
    bool Tallies = false,
    bool PicksOne = false,
    bool NamesAnswers = true)
{
    /// <summary>The words, by the one a fence opened with. Adding a question is adding a
    /// row here and the same word to Core's set — there is no third place.</summary>
    public static readonly IReadOnlyDictionary<string, ListVocabulary> All =
        new Dictionary<string, ListVocabulary>(StringComparer.OrdinalIgnoreCase)
        {
            ["collection"] = new(
                Rows: "Item",
                Total: "{0} to collect",
                Score: "You have {0} of {1} — {2} still to find.",
                Invite: "Check anything to start your own list.",
                Yes: "has this",
                No: "does not have this"),
            ["availability"] = new(
                Rows: "When",
                Total: "{0} slots",
                Score: "You can make {0} of {1}.",
                Invite: "Check the ones you can make.",
                Yes: "can make it",
                No: "cannot make it",
                Tallies: true),
            ["poll"] = new(
                Rows: "Option",
                Total: "{0} options",
                Score: "Your answer is counted.",
                Invite: "Pick one.",
                Yes: "picked this",
                No: "did not pick this",
                Tallies: true,
                PicksOne: true,
                NamesAnswers: false),
        };

    /// <summary>The words for a list, falling back to the commonest question rather than
    /// to nothing: a word this build has never heard of still renders a grid.</summary>
    public static ListVocabulary For(string? kind) =>
        kind is not null && All.TryGetValue(kind, out var found) ? found : All["collection"];
}
