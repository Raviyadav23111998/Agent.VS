using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace Agent.ToolWindow
{
    [Guid("98d41e72-cb25-41e9-9136-24e50eb1c8cb")]
    public class AgentToolWindow : ToolWindowPane
    {
        public AgentToolWindow() : base(null)
        {
            this.Caption = "Agent";
            this.Content = new AgentToolWindowControl();
        }
    }
}
