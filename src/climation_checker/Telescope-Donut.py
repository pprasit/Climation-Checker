import cv2
import numpy as np
import math
from astropy.io import fits
from astropy.stats import sigma_clipped_stats

def measure_donut_auto(image_path):
    # 1. Load the FITS image
    try:
        hdul = fits.open(image_path)
        image_data = hdul[0].data
        hdul.close()
    except Exception as e:
        print(f"Error: Could not load FITS image. Details: {e}")
        return

    if image_data is None:
        print("Error: FITS file contains no data.")
        return

    # 2. Dynamically determine image scale based on a 2048x2048 baseline
    height, width = image_data.shape
    scale_factor = max(width, height) / 4096.0

    # Dynamic geometric constraints scaled to the image resolution
    max_outer_radius = 300 * scale_factor
    max_inner_radius = 100 * scale_factor
    max_center_offset = 50 * scale_factor

    # 3. SIGMA CLIPPING: Calculate true background stats to preserve raw edges
    image_data = np.nan_to_num(image_data)
    
    # This evaluates the image and ignores extreme bright/dark pixels to find the true background
    mean, median, std = sigma_clipped_stats(image_data, sigma=3.0, maxiters=5)
    
    # Normalize using the sigma-clipped median as absolute black.
    # This stretches the contrast perfectly without needing to blur the image.
    vmin = median
    vmax = np.percentile(image_data, 99.9)
    
    image_data_clipped = np.clip(image_data, vmin, vmax)
    if vmax > vmin:
        gray = ((image_data_clipped - vmin) / (vmax - vmin) * 255.0).astype(np.uint8)
    else:
        gray = np.zeros_like(image_data, dtype=np.uint8)

    # Create a 3-channel canvas for drawing
    img = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)
    
    # 4. Apply Auto-Thresholding directly on the UNBLURRED, sharp image
    thresh_val, thresh = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    
    # 5. Find Contours
    contours, hierarchy = cv2.findContours(thresh, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_SIMPLE)
    contours = sorted(contours, key=cv2.contourArea, reverse=True)
    
    # 6. Gather all valid ellipses
    valid_ellipses = []
    for c in contours:
        if len(c) >= 5:
            # cv2.fitEllipse uses least-squares fitting, which naturally smooths over
            # single-pixel edge noise without needing the image to be blurred beforehand.
            ellipse = cv2.fitEllipse(c)
            (cx, cy), (d1, d2), angle = ellipse
            a = max(d1, d2) / 2.0
            b = min(d1, d2) / 2.0
            valid_ellipses.append({
                'contour': c, 'ellipse': ellipse, 'cx': cx, 'cy': cy, 'a': a, 'b': b, 'angle': angle
            })

    outer_cand = None
    inner_cand = None
    found_pair = False

    # 7. Search for a pair matching the dynamic geometric criteria
    for i in range(len(valid_ellipses)):
        for j in range(i + 1, len(valid_ellipses)):
            e1 = valid_ellipses[i]
            e2 = valid_ellipses[j]
            
            # Calculate Center Offset
            dist = math.sqrt((e1['cx'] - e2['cx'])**2 + (e1['cy'] - e2['cy'])**2)
            
            if dist < max_center_offset:
                if e1['a'] > e2['a']:
                    out_e, in_e = e1, e2
                else:
                    out_e, in_e = e2, e1
                    
                if out_e['a'] < max_outer_radius and in_e['a'] < max_inner_radius:
                    outer_cand = out_e
                    inner_cand = in_e
                    found_pair = True
                    break
        if found_pair:
            break

    if not found_pair:
        print(f"Error: Could not find pair matching sizes (<{max_outer_radius:.1f}, <{max_inner_radius:.1f}) and offset (<{max_center_offset:.1f}).")
        return

    # 8. Calculate extra metrics for legend
    out_elongation = outer_cand['a'] / outer_cand['b'] if outer_cand['b'] != 0 else 0
    out_ellipticity = 1.0 - (outer_cand['b'] / outer_cand['a']) if outer_cand['a'] != 0 else 0
    
    in_elongation = inner_cand['a'] / inner_cand['b'] if inner_cand['b'] != 0 else 0
    in_ellipticity = 1.0 - (inner_cand['b'] / inner_cand['a']) if inner_cand['a'] != 0 else 0

    # 9. Dynamic Legend Scaling
    font_scale = 1.0 * scale_factor
    font_thickness = max(2, int(3 * scale_factor))
    line_thickness = max(2, int(3 * scale_factor))
    
    global text_y
    text_y = int(60 * scale_factor)
    spacing = int(45 * scale_factor)

    def draw_text(text, color=(255, 255, 255)):
        global text_y
        cv2.putText(img, text, (int(30 * scale_factor), text_y), cv2.FONT_HERSHEY_SIMPLEX, font_scale, color, font_thickness)
        text_y += spacing

    # --- DRAWING ---
    # Draw Outer Boundary (Green)
    cv2.ellipse(img, outer_cand['ellipse'], (0, 255, 0), line_thickness)
    # Draw Inner Boundary (Yellow)
    cv2.ellipse(img, inner_cand['ellipse'], (0, 255, 255), line_thickness)

    # --- LEGEND ---
    # Outer metrics (Green)
    draw_text("--- Outer Boundary ---", (0, 255, 0))
    draw_text(f"Center (X, Y):      ({outer_cand['cx']:.2f}, {outer_cand['cy']:.2f})", (0, 255, 0))
    draw_text(f"Semi-major axis (A): {outer_cand['a']:.2f} pixels", (0, 255, 0))
    draw_text(f"Semi-minor axis (B): {outer_cand['b']:.2f} pixels", (0, 255, 0))
    draw_text(f"Position Angle (THETA): {outer_cand['angle']:.2f} degrees", (0, 255, 0))
    draw_text(f"ELONGATION (A/B):   {out_elongation:.4f}", (0, 255, 0))
    draw_text(f"ELLIPTICITY (1-B/A): {out_ellipticity:.4f}", (0, 255, 0))
    draw_text(" ", (0,0,0)) # Spacer

    # Inner metrics (Yellow)
    draw_text("--- Inner Boundary (Hole) ---", (0, 255, 255))
    draw_text(f"Center (X, Y):      ({inner_cand['cx']:.2f}, {inner_cand['cy']:.2f})", (0, 255, 255))
    draw_text(f"Semi-major axis (A): {inner_cand['a']:.2f} pixels", (0, 255, 255))
    draw_text(f"Semi-minor axis (B): {inner_cand['b']:.2f} pixels", (0, 255, 255))
    draw_text(f"Position Angle (THETA): {inner_cand['angle']:.2f} degrees", (0, 255, 255))
    draw_text(f"ELONGATION (A/B):   {in_elongation:.4f}", (0, 255, 255))
    draw_text(f"ELLIPTICITY (1-B/A): {in_ellipticity:.4f}", (0, 255, 255))
    
    # 10. Save the result
    output_filename = "measured_donut.jpg"
    cv2.imwrite(output_filename, img)
    print(f"Success! Visualized output saved to: {output_filename}")

# Run the function
measure_donut_auto('Procyon-01.fit')
