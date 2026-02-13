﻿using Soapimane.Other;
using System.Windows;

namespace Soapimane.MouseMovementLibraries.GHubSupport
{
    internal class LGHubMain
    {
        public bool Load()
        {
            if (!RequirementsManager.CheckForGhub())
            {
                MessageBox.Show("Unfortunately, LG HUB Mouse is not here.", "Soapimane");
                return false;
            }

            if (RequirementsManager.IsMemoryIntegrityEnabled())
            {
                try
                {
                    LGMouse.Open();
                    LGMouse.Close();
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unfortunately, LG HUB Mouse Movement mode cannot be ran sufficiently.\n" + ex.ToString(), "Soapimane");
                    return false;
                }
            }
            else
            {
                MessageBox.Show("Memory Integrity is enabled. Please disable it to use LG HUB Mouse Movement mode.", "Soapimane");
                return false;
            }
        }
    }
}