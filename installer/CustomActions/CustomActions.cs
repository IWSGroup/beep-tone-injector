using WixToolset.Dtf.WindowsInstaller;

namespace BeepTone.Setup
{
    public static class CustomActions
    {
        // Turns the password typed on the setup password page (or passed as SETUPPASSWORD) into the
        // hash the tray checks. Only the hash is written to the registry; the password is cleared.
        [CustomAction]
        public static ActionResult HashSetupPassword(Session session)
        {
            string password = session["SETUPPASSWORD"];
            if (string.IsNullOrEmpty(password)) return ActionResult.Success;
            session["SETUPPASSWORDHASH"] = SetupPassword.Hash(password);
            session["SETUPPASSWORD"] = "";
            session["SETUPPASSWORDCONFIRM"] = "";
            session.Log("A new setup password was set.");
            return ActionResult.Success;
        }
    }
}
