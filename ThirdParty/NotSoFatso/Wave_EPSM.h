/*
 * Copyright (C) 2004      Disch
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

 //////////////////////////////////////////////////////////////////////////
 //
 //  Wave_FME07.h
 //
 //

class EPSMWave
{
public:
	/////////////////////////////
	// Frequency Control
	TWIN		nFreqTimer;
	int			nFreqCount;

	/////////////////////////////
	// Channel Disabling
	BYTE		bChannelEnabled;
	BYTE		bChannelMixer;

	/////////////////////////////
	// Noise
	BYTE		bNoiseFrequency;

	/////////////////////////////
	// Fm Stuff
	BYTE        nTriggered;
	BYTE        nStereo;
	BYTE        nPatchReg[31];

	/////////////////////////////
	// Volume
	BYTE		nVolume;

	/////////////////////////////
// Envelope
	TWIN		nEnvFreq;
	BYTE		bEnvelopeEnabled;
	BYTE		bEnvelopeTriggered;
	BYTE		nEnvelopeShape;

	/////////////////////////////
	// Duty Cycle
	BYTE		nDutyCount;

	///////////////////////////////////
	// Output and Downsampling
	short		nOutputTable_L[0x10];
	short		nOutputTable_R[0x10];
	int			nMixL;
	int			nMixR;

	///////////////////////////////////
	// Inversion
	BYTE		bInvert;
	BYTE		bDoInvert;
	WORD		nInvertFreqCutoff;


	///////////////////////////////////////////////////////////////////
	///////////////////////////////////////////////////////////////////
	//  Functions
	///////////////////////////////////////////////////////////////////
	///////////////////////////////////////////////////////////////////

	FORCEINLINE void DoTicks(int ticks, BYTE mix)
	{
		register int mn;

		if (!bChannelEnabled)		return;
		if (!nFreqTimer.W)			return;

		while (ticks)
		{
			mn = min(nFreqCount, ticks);
			ticks -= mn;

			nFreqCount -= mn;

			if (mix && (nDutyCount < 16))
			{
				nMixL += nOutputTable_L[nVolume] * mn;
				nMixR += nOutputTable_R[nVolume] * (bDoInvert ? -mn : mn);
			}

			if (nFreqCount > 0) continue;
			nFreqCount = nFreqTimer.W;

			if (++nDutyCount >= 32)
			{
				bDoInvert = bInvert && (nFreqTimer.W < nInvertFreqCutoff);
				nDutyCount = 0;
			}
		}
	}

	FORCEINLINE void Mix_Mono(int& mix, int downsample)
	{
		mix += (nMixL / downsample);
		nMixL = 0;
	}

	FORCEINLINE void Mix_Stereo(int& mixL, int& mixR, int downsample)
	{
		mixL += (nMixL / downsample);
		mixR += (nMixR / downsample);

		nMixL = nMixR = 0;
	}
};