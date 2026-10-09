namespace Assistant.Core.Confirmation;

/// <summary>
/// What a call that needs the user's confirmation would do, in the few kinds that matter to the user (PROJECT_SPEC §4.8, step 115). The kind is
/// fixed by the tool's own code, never by what the model wrote; it decides how the question is worded and how it is logged (the kind, never what is
/// in the call).
/// </summary>
public enum ConfirmationKind
{
    /// <summary>A call whose effect no other kind describes: the question shows the tool and every argument as it was given.</summary>
    Other = 0,

    /// <summary>A message to a person, which cannot be taken back once it is sent.</summary>
    SendMessage = 1,

    /// <summary>Files or folders deleted, moved, renamed or changed.</summary>
    ChangeFiles = 2,

    /// <summary>A program started, or a file opened with the program that handles it, which may itself do anything.</summary>
    Launch = 3,

    /// <summary>The user's content sent off this PC, to a service or a server.</summary>
    Upload = 4,

    /// <summary>Something changed in a connected app or an account: a task created or deleted, data edited, a setting changed.</summary>
    ConnectedApp = 5,

    /// <summary>A setting of this PC changed, such as the sound.</summary>
    ChangeSystem = 6,

    /// <summary>Something of the screen taken into the conversation.</summary>
    Capture = 7,

    /// <summary>
    /// Access to something of the user's, asked about before a tool that only reads it runs (step 119): the capability is set to ask every time, so each use is a question
    /// of its own. What the tool does is asked separately, when it changes anything.
    /// </summary>
    Access = 8,
}
