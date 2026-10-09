namespace Assistant.Core.ModelHosting;

/// <summary>A message the model host sends to the app, answering the request whose id it carries.</summary>
public abstract record ModelHostReply : ModelHostMessage;
